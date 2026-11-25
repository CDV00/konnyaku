/**
 * WebRTC mesh of the built-in classroom: every participant sends audio/video to every other one directly.
 * The signaling service (services/signaling) only introduces peers and relays SDP/ICE; the newcomer makes the offers.
 * Media changes (mic, camera, screen) replace tracks on existing transceivers, so no renegotiation is needed.
 * No React here: the UI reads snapshots through useSyncExternalStore.
 */

export type MediaState = { audio: boolean; video: boolean; screen: boolean };

export type Role = "host" | "learner";

export type PeerView = {
  id: string;
  userId: string;
  name: string;
  role: Role;
  state: MediaState;
  stream: MediaStream;
  connection: RTCPeerConnectionState;
};

export type ChatMessage = { id: string; name: string; text: string; at: number; self: boolean };

export type Phase = "connecting" | "live" | "reconnecting" | "ended" | "failed" | "left";

export type Snapshot = {
  phase: Phase;
  /** Error code (translated by the UI) when phase is "failed". */
  error?: string;
  selfId?: string;
  role?: Role;
  sessionId?: string;
  capacity?: number;
  peers: PeerView[];
  chat: ChatMessage[];
  local: MediaState;
  /** What this participant sends as video (camera or screen), for the self view. */
  localStream: MediaStream | null;
};

export type TicketResponse = {
  sessionId: string;
  ticket: string;
  signalingUrl: string;
  iceServers: { urls: string[]; username?: string | null; credential?: string | null }[];
  role: Role;
};

/** Result of the ticket route: a ticket, or an API error code (`workspace.room_ended`...). */
export type TicketResult = { ok: true; value: TicketResponse } | { ok: false; code: string };

export type LocalMediaInit = {
  audioTrack: MediaStreamTrack | null;
  cameraTrack: MediaStreamTrack | null;
  micOn: boolean;
  audioDeviceId?: string;
  videoDeviceId?: string;
};

type PeerInfo = { id: string; userId: string; name: string; role: Role; state: MediaState };

type Peer = {
  info: PeerInfo;
  pc: RTCPeerConnection;
  chat: RTCDataChannel;
  stream: MediaStream;
  initiator: boolean;
  configured: boolean;
  pending: RTCIceCandidateInit[];
  /** Signals of one peer are handled one after another. */
  queue: Promise<void>;
};

type ServerMessage =
  | { type: "welcome"; self: PeerInfo; peers: PeerInfo[]; capacity: number }
  | { type: "peer-joined"; peer: PeerInfo }
  | { type: "peer-left"; id: string }
  | { type: "signal"; from: string; data: SignalData }
  | { type: "state"; id: string; state: MediaState }
  | { type: "ended" }
  | { type: "error"; code: string };

type SignalData = { description?: RTCSessionDescriptionInit; candidate?: RTCIceCandidateInit };

const MAX_CHAT = 1000;
const VIDEO_CONSTRAINTS: MediaTrackConstraints = { width: { ideal: 640 }, height: { ideal: 360 }, frameRate: { ideal: 24, max: 30 } };

export function videoConstraints(deviceId?: string): MediaTrackConstraints {
  return deviceId ? { ...VIDEO_CONSTRAINTS, deviceId: { exact: deviceId } } : VIDEO_CONSTRAINTS;
}

export function audioConstraints(deviceId?: string): MediaTrackConstraints {
  const base = { echoCancellation: true, noiseSuppression: true, autoGainControl: true };
  return deviceId ? { ...base, deviceId: { exact: deviceId } } : base;
}

/** A path such as /rtc/ws is served by the same host as the page (behind Caddy); dev uses an absolute ws:// URL. */
function resolveSignalingUrl(url: string): string {
  if (/^wss?:\/\//.test(url)) return url;
  const base = new URL(url, window.location.href);
  base.protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  return base.toString();
}

export class RoomClient {
  private snapshot: Snapshot;
  private readonly listeners = new Set<() => void>();
  private readonly peers = new Map<string, Peer>();
  private ws: WebSocket | null = null;
  private iceServers: RTCIceServer[] = [];
  private audioTrack: MediaStreamTrack | null;
  private cameraTrack: MediaStreamTrack | null;
  private screenTrack: MediaStreamTrack | null = null;
  private readonly audioDeviceId?: string;
  private readonly videoDeviceId?: string;
  private attempt = 0;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private stopped = false;

  constructor(
    private readonly getTicket: () => Promise<TicketResult>,
    media: LocalMediaInit,
  ) {
    this.audioTrack = media.audioTrack;
    this.cameraTrack = media.cameraTrack;
    this.audioDeviceId = media.audioDeviceId;
    this.videoDeviceId = media.videoDeviceId;
    if (this.audioTrack) this.audioTrack.enabled = media.micOn;
    this.snapshot = {
      phase: "connecting",
      peers: [],
      chat: [],
      local: { audio: !!this.audioTrack && media.micOn, video: !!this.cameraTrack, screen: false },
      localStream: this.cameraTrack ? new MediaStream([this.cameraTrack]) : null,
    };
  }

  // ---- store -------------------------------------------------------------------------------------------------

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  getSnapshot = () => this.snapshot;

  private update(patch: Partial<Snapshot>) {
    this.snapshot = { ...this.snapshot, ...patch };
    for (const listener of this.listeners) listener();
  }

  private publishPeers() {
    this.update({
      peers: [...this.peers.values()].map((p) => ({ ...p.info, stream: p.stream, connection: p.pc.connectionState })),
    });
    this.applyBitrate();
  }

  // ---- connection -------------------------------------------------------------------------------------------------

  start() {
    window.addEventListener("online", this.onOnline);
    void this.connect();
  }

  private onOnline = () => {
    if (this.snapshot.phase === "reconnecting") {
      if (this.retryTimer) clearTimeout(this.retryTimer);
      void this.connect();
    }
  };

  private async connect() {
    if (this.stopped) return;
    this.retryTimer = null;
    let result: TicketResult;
    try {
      result = await this.getTicket();
    } catch {
      this.scheduleReconnect();
      return;
    }
    if (this.stopped) return;
    if (!result.ok) {
      if (result.code === "workspace.room_ended") this.finish("ended");
      else if (result.code === "network") this.scheduleReconnect();
      else this.finish("failed", result.code);
      return;
    }

    const ticket = result.value;
    this.iceServers = ticket.iceServers.map((s) => ({ urls: s.urls, username: s.username ?? undefined, credential: s.credential ?? undefined }));
    this.update({ sessionId: ticket.sessionId, role: ticket.role });

    const ws = new WebSocket(resolveSignalingUrl(ticket.signalingUrl));
    this.ws = ws;
    ws.onopen = () => ws.send(JSON.stringify({ type: "join", ticket: ticket.ticket, state: this.snapshot.local }));
    ws.onmessage = (event) => {
      if (this.ws !== ws || typeof event.data !== "string") return;
      try {
        this.onMessage(JSON.parse(event.data) as ServerMessage);
      } catch (error) {
        console.warn("room: bad message", error);
      }
    };
    ws.onclose = () => {
      if (this.ws !== ws) return;
      this.ws = null;
      this.closePeers();
      if (!this.stopped) this.scheduleReconnect();
    };
  }

  private scheduleReconnect() {
    if (this.stopped || this.retryTimer) return;
    this.attempt += 1;
    this.update({ phase: this.snapshot.selfId ? "reconnecting" : "connecting" });
    const delay = Math.min(10_000, 1000 * 2 ** Math.min(this.attempt - 1, 4));
    this.retryTimer = setTimeout(() => void this.connect(), delay);
  }

  private onMessage(message: ServerMessage) {
    switch (message.type) {
      case "welcome":
        this.attempt = 0;
        this.update({ phase: "live", selfId: message.self.id, capacity: message.capacity });
        for (const info of message.peers) this.addPeer(info, true);
        this.publishPeers();
        break;
      case "peer-joined":
        this.addPeer(message.peer, false);
        this.publishPeers();
        break;
      case "peer-left":
        this.removePeer(message.id);
        this.publishPeers();
        break;
      case "signal": {
        const peer = this.peers.get(message.from);
        if (peer) peer.queue = peer.queue.then(() => this.onSignal(peer, message.data)).catch((e) => console.warn("room: signal", e));
        break;
      }
      case "state": {
        const peer = this.peers.get(message.id);
        if (peer) {
          peer.info = { ...peer.info, state: message.state };
          this.publishPeers();
        }
        break;
      }
      case "ended":
        this.finish("ended");
        break;
      case "error":
        if (message.code === "room_ended") this.finish("ended");
        else if (message.code === "room_full" || message.code === "replaced") this.finish("failed", message.code);
        // Expired/invalid ticket: the server closes, the reconnect fetches a new ticket.
        break;
    }
  }

  private send(message: object) {
    if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
  }

  // ---- peers -----------------------------------------------------------------------------------------------------

  private outgoingVideo(): MediaStreamTrack | null {
    return this.screenTrack ?? this.cameraTrack;
  }

  private addPeer(info: PeerInfo, initiator: boolean) {
    this.removePeer(info.id);
    const pc = new RTCPeerConnection({ iceServers: this.iceServers });
    // Negotiated channel: both sides open it themselves, no extra round trip.
    const chat = pc.createDataChannel("chat", { negotiated: true, id: 0 });
    const peer: Peer = { info, pc, chat, stream: new MediaStream(), initiator, configured: initiator, pending: [], queue: Promise.resolve() };
    this.peers.set(info.id, peer);

    chat.onmessage = (event) => this.onChat(peer, event.data);
    pc.ontrack = (event) => {
      // A new stream object makes the video element pick the track up.
      peer.stream = new MediaStream([...peer.stream.getTracks().filter((t) => t.kind !== event.track.kind), event.track]);
      this.publishPeers();
    };
    pc.onicecandidate = (event) => {
      if (event.candidate) this.send({ type: "signal", to: info.id, data: { candidate: event.candidate.toJSON() } });
    };
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === "failed" && peer.initiator) {
        peer.queue = peer.queue.then(() => this.offer(peer, true)).catch((e) => console.warn("room: ice restart", e));
      }
      this.publishPeers();
    };

    if (initiator) {
      pc.addTransceiver(this.audioTrack ?? "audio", { direction: "sendrecv" });
      pc.addTransceiver(this.outgoingVideo() ?? "video", { direction: "sendrecv" });
      peer.queue = peer.queue.then(() => this.offer(peer, false)).catch((e) => console.warn("room: offer", e));
    }
  }

  private async offer(peer: Peer, iceRestart: boolean) {
    const offer = await peer.pc.createOffer({ iceRestart });
    await peer.pc.setLocalDescription(offer);
    this.send({ type: "signal", to: peer.info.id, data: { description: peer.pc.localDescription?.toJSON() } });
  }

  private async onSignal(peer: Peer, data: SignalData) {
    const { pc } = peer;
    if (data.description) {
      await pc.setRemoteDescription(data.description);
      if (data.description.type === "offer") {
        if (!peer.configured) {
          // Answerer: send on the transceivers the offer created (one audio, one video).
          for (const transceiver of pc.getTransceivers()) {
            const kind = transceiver.receiver.track.kind;
            transceiver.direction = "sendrecv";
            await transceiver.sender.replaceTrack(kind === "audio" ? this.audioTrack : this.outgoingVideo());
          }
          peer.configured = true;
        }
        await pc.setLocalDescription(await pc.createAnswer());
        this.send({ type: "signal", to: peer.info.id, data: { description: pc.localDescription?.toJSON() } });
      }
      for (const candidate of peer.pending.splice(0)) await pc.addIceCandidate(candidate).catch(() => undefined);
    } else if (data.candidate) {
      if (pc.remoteDescription) await pc.addIceCandidate(data.candidate).catch(() => undefined);
      else peer.pending.push(data.candidate);
    }
  }

  private removePeer(id: string) {
    const peer = this.peers.get(id);
    if (!peer) return;
    this.peers.delete(id);
    peer.chat.close();
    peer.pc.close();
  }

  private closePeers() {
    for (const id of [...this.peers.keys()]) this.removePeer(id);
    this.publishPeers();
  }

  /** Mesh: everyone uploads one stream per peer, so the bitrate goes down as the room fills. */
  private applyBitrate() {
    const maxBitrate = this.peers.size <= 1 ? 1_200_000 : this.peers.size <= 3 ? 700_000 : 400_000;
    for (const peer of this.peers.values()) {
      const sender = peer.pc.getTransceivers().find((t) => t.receiver.track.kind === "video")?.sender;
      if (!sender) continue;
      const parameters = sender.getParameters();
      if (!parameters.encodings?.length || parameters.encodings[0]?.maxBitrate === maxBitrate) continue;
      parameters.encodings[0]!.maxBitrate = maxBitrate;
      sender.setParameters(parameters).catch(() => undefined);
    }
  }

  private async replaceOutgoing(kind: "audio" | "video") {
    const track = kind === "audio" ? this.audioTrack : this.outgoingVideo();
    await Promise.all(
      [...this.peers.values()]
        .filter((p) => p.configured)
        .flatMap((p) => p.pc.getTransceivers().filter((t) => t.receiver.track.kind === kind))
        .map((t) => t.sender.replaceTrack(track).catch(() => undefined)),
    );
  }

  private publishLocal(patch: Partial<MediaState>) {
    const local = { ...this.snapshot.local, ...patch };
    const video = this.outgoingVideo();
    this.update({ local, localStream: video ? new MediaStream([video]) : null });
    this.send({ type: "state", ...local });
  }

  // ---- chat ------------------------------------------------------------------------------------------------------

  private onChat(peer: Peer, raw: unknown) {
    if (typeof raw !== "string") return;
    try {
      const message = JSON.parse(raw) as { type?: string; id?: string; text?: string };
      if (message.type !== "chat" || typeof message.text !== "string" || !message.text.trim()) return;
      // The name comes from the ticket (via the signaling service), never from the message itself.
      this.update({
        chat: [
          ...this.snapshot.chat,
          { id: String(message.id ?? crypto.randomUUID()), name: peer.info.name, text: message.text.slice(0, MAX_CHAT), at: Date.now(), self: false },
        ],
      });
    } catch {
      // Ignore malformed messages.
    }
  }

  sendChat(text: string, selfName: string) {
    const trimmed = text.trim().slice(0, MAX_CHAT);
    if (!trimmed) return;
    const message = { type: "chat", id: crypto.randomUUID(), text: trimmed };
    for (const peer of this.peers.values()) {
      if (peer.chat.readyState === "open") peer.chat.send(JSON.stringify(message));
    }
    this.update({ chat: [...this.snapshot.chat, { id: message.id, name: selfName, text: trimmed, at: Date.now(), self: true }] });
  }

  // ---- local media -------------------------------------------------------------------------------------------------

  async setMic(on: boolean) {
    if (on && !this.audioTrack) {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: audioConstraints(this.audioDeviceId) });
      this.audioTrack = stream.getAudioTracks()[0] ?? null;
      await this.replaceOutgoing("audio");
    }
    if (this.audioTrack) this.audioTrack.enabled = on;
    this.publishLocal({ audio: on && !!this.audioTrack });
  }

  async setCamera(on: boolean) {
    if (on && !this.cameraTrack) {
      const stream = await navigator.mediaDevices.getUserMedia({ video: videoConstraints(this.videoDeviceId) });
      this.cameraTrack = stream.getVideoTracks()[0] ?? null;
    } else if (!on && this.cameraTrack) {
      // Stopping (not just disabling) turns the camera light off.
      this.cameraTrack.stop();
      this.cameraTrack = null;
    }
    await this.replaceOutgoing("video");
    this.publishLocal({ video: !!this.cameraTrack });
  }

  async setScreen(on: boolean) {
    if (on && !this.screenTrack) {
      const stream = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: { ideal: 15, max: 30 } }, audio: false });
      const track = stream.getVideoTracks()[0];
      if (!track) return;
      track.contentHint = "detail";
      // "Stop sharing" in the browser's own bar.
      track.onended = () => void this.setScreen(false);
      this.screenTrack = track;
    } else if (!on && this.screenTrack) {
      this.screenTrack.onended = null;
      this.screenTrack.stop();
      this.screenTrack = null;
    }
    await this.replaceOutgoing("video");
    this.publishLocal({ screen: !!this.screenTrack });
  }

  // ---- leaving -----------------------------------------------------------------------------------------------------

  /** Teacher: closes the room for everyone; the server answers with "ended". */
  endForAll() {
    this.send({ type: "end" });
  }

  leave() {
    if (this.stopped) return;
    this.send({ type: "leave" });
    this.finish("left");
  }

  private finish(phase: "ended" | "failed" | "left", error?: string) {
    this.stopped = true;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    window.removeEventListener("online", this.onOnline);
    const ws = this.ws;
    this.ws = null;
    ws?.close();
    for (const id of [...this.peers.keys()]) this.removePeer(id);
    for (const track of [this.audioTrack, this.cameraTrack, this.screenTrack]) track?.stop();
    this.audioTrack = this.cameraTrack = this.screenTrack = null;
    this.update({ phase, error, peers: [], localStream: null });
  }
}
