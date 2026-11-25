"use client";

import { useEffect, useRef, useState, useSyncExternalStore, type CSSProperties, type FormEvent, type ReactNode } from "react";
import { useFormatter, useTranslations } from "next-intl";
import {
  CircleCheck,
  DoorOpen,
  Loader2,
  MessageSquare,
  Mic,
  MicOff,
  MonitorUp,
  PhoneOff,
  Send,
  Users,
  Video,
  VideoOff,
  WifiOff,
  X,
} from "lucide-react";
import type { Realm } from "@konnyaku/session";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { cn } from "@konnyaku/ui/lib/utils";
import {
  RoomClient,
  audioConstraints,
  videoConstraints,
  type ChatMessage,
  type LocalMediaInit,
  type MediaState,
  type Role,
  type Snapshot,
  type TicketResult,
} from "./room-client";

type Props = { code: string; realm: Realm; home: string; selfName: string };

/** Lobby (camera/mic check) then the room. The client is created on "Join", so the room starts from a click. */
export function Classroom({ code, realm, home, selfName }: Props) {
  const [client, setClient] = useState<RoomClient | null>(null);

  function join(media: LocalMediaInit) {
    const getTicket = async (): Promise<TicketResult> => {
      let response: Response;
      try {
        response = await fetch("/room/api/ticket", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ code, realm }),
          cache: "no-store",
        });
      } catch {
        return { ok: false, code: "network" };
      }
      if (response.ok) return { ok: true, value: await response.json() };
      if (response.status >= 500) return { ok: false, code: "network" };
      const body = (await response.json().catch(() => ({}))) as { code?: string };
      return { ok: false, code: body.code ?? "unknown" };
    };
    const room = new RoomClient(getTicket, media);
    room.start();
    setClient(room);
  }

  return client ? <Room client={client} home={home} selfName={selfName} /> : <Lobby onJoin={join} />;
}

// ---- lobby -------------------------------------------------------------------------------------------------------

type DeviceError = "denied" | "missing" | null;

function mediaError(error: unknown): DeviceError {
  return error instanceof DOMException && (error.name === "NotAllowedError" || error.name === "SecurityError") ? "denied" : "missing";
}

function Lobby({ onJoin }: { onJoin: (media: LocalMediaInit) => void }) {
  const t = useTranslations("room");
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([]);
  const [audioId, setAudioId] = useState("");
  const [videoId, setVideoId] = useState("");
  const [micOn, setMicOn] = useState(true);
  const [camOn, setCamOn] = useState(true);
  const [audioTrack, setAudioTrack] = useState<MediaStreamTrack | null>(null);
  const [videoTrack, setVideoTrack] = useState<MediaStreamTrack | null>(null);
  const [micError, setMicError] = useState<DeviceError>(null);
  const [camError, setCamError] = useState<DeviceError>(null);
  const [joining, setJoining] = useState(false);
  // Tracks handed to the room must survive the lobby's cleanup.
  const handedOver = useRef(false);
  const meter = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    const refresh = () => navigator.mediaDevices?.enumerateDevices().then(setDevices).catch(() => undefined);
    navigator.mediaDevices?.addEventListener("devicechange", refresh);
    return () => navigator.mediaDevices?.removeEventListener("devicechange", refresh);
  }, []);

  useEffect(() => {
    let track: MediaStreamTrack | null = null;
    let cancelled = false;
    navigator.mediaDevices
      ?.getUserMedia({ audio: audioConstraints(audioId || undefined) })
      .then((stream) => {
        track = stream.getAudioTracks()[0] ?? null;
        if (cancelled) return track?.stop();
        setAudioTrack(track);
        setMicError(null);
        return navigator.mediaDevices.enumerateDevices().then(setDevices);
      })
      .catch((error: unknown) => {
        if (!cancelled) setMicError(mediaError(error));
      });
    return () => {
      cancelled = true;
      if (!handedOver.current) track?.stop();
    };
  }, [audioId]);

  useEffect(() => {
    if (!camOn) return;
    let track: MediaStreamTrack | null = null;
    let cancelled = false;
    navigator.mediaDevices
      ?.getUserMedia({ video: videoConstraints(videoId || undefined) })
      .then((stream) => {
        track = stream.getVideoTracks()[0] ?? null;
        if (cancelled) return track?.stop();
        setVideoTrack(track);
        setCamError(null);
        return navigator.mediaDevices.enumerateDevices().then(setDevices);
      })
      .catch((error: unknown) => {
        if (!cancelled) setCamError(mediaError(error));
      });
    return () => {
      cancelled = true;
      if (!handedOver.current) track?.stop();
    };
  }, [videoId, camOn]);

  // Microphone level: drawn straight into the DOM, no re-render per frame.
  useEffect(() => {
    if (!audioTrack || !micOn) return;
    const context = new AudioContext();
    const analyser = context.createAnalyser();
    analyser.fftSize = 512;
    context.createMediaStreamSource(new MediaStream([audioTrack])).connect(analyser);
    const samples = new Uint8Array(analyser.fftSize);
    let frame = 0;
    const draw = () => {
      analyser.getByteTimeDomainData(samples);
      let peak = 0;
      for (const s of samples) peak = Math.max(peak, Math.abs(s - 128));
      if (meter.current) meter.current.style.transform = `scaleX(${Math.min(1, peak / 64)})`;
      frame = requestAnimationFrame(draw);
    };
    draw();
    return () => {
      cancelAnimationFrame(frame);
      void context.close();
    };
  }, [audioTrack, micOn]);

  const cameraTrack = camOn && videoTrack?.readyState === "live" ? videoTrack : null;
  const previewStream = cameraTrack ? new MediaStream([cameraTrack]) : null;
  const mics = devices.filter((d) => d.kind === "audioinput");
  const cams = devices.filter((d) => d.kind === "videoinput");

  function toggleCamera() {
    if (camOn) {
      videoTrack?.stop();
      setVideoTrack(null);
    }
    setCamOn(!camOn);
  }

  function join() {
    handedOver.current = true;
    setJoining(true);
    onJoin({ audioTrack, cameraTrack, micOn, audioDeviceId: audioId || undefined, videoDeviceId: videoId || undefined });
  }

  return (
    <main className="mx-auto flex h-full max-w-5xl flex-col justify-center gap-6 overflow-y-auto px-4 py-8 sm:px-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">{t("lobby.heading")}</h1>
        <p className="text-sm text-muted-foreground">{t("lobby.hint")}</p>
      </header>
      <div className="grid gap-6 md:grid-cols-[1.5fr_1fr]">
        <div className="relative aspect-video overflow-hidden rounded-2xl border bg-muted">
          {previewStream ? (
            <VideoView stream={previewStream} muted mirror className="size-full object-cover" />
          ) : (
            <div className="grid size-full place-items-center text-muted-foreground">
              <span className="flex flex-col items-center gap-2 text-sm">
                <VideoOff className="size-8" />
                {camError === "denied" ? t("lobby.cameraDenied") : camError ? t("lobby.cameraMissing") : t("lobby.cameraOff")}
              </span>
            </div>
          )}
          <div className="absolute inset-x-0 bottom-0 flex justify-center gap-3 p-3">
            <RoundButton on={micOn && !!audioTrack} disabled={!audioTrack} onClick={() => setMicOn(!micOn)} label={micOn ? t("controls.micOn") : t("controls.micOff")}>
              {micOn && audioTrack ? <Mic className="size-5" /> : <MicOff className="size-5" />}
            </RoundButton>
            <RoundButton on={!!cameraTrack} disabled={camError === "denied"} onClick={toggleCamera} label={camOn ? t("controls.camOn") : t("controls.camOff")}>
              {cameraTrack ? <Video className="size-5" /> : <VideoOff className="size-5" />}
            </RoundButton>
          </div>
        </div>
        <div className="flex flex-col gap-4">
          <Field label={t("lobby.microphone")}>
            <NativeSelect value={audioId} onChange={(e) => setAudioId(e.target.value)} disabled={mics.length === 0}>
              <option value="">{t("lobby.defaultDevice")}</option>
              {mics.map((d, i) => (
                <option key={d.deviceId || i} value={d.deviceId}>
                  {d.label || t("lobby.deviceNumber", { number: i + 1 })}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <div className="space-y-1.5">
            <span className="text-xs text-muted-foreground">{t("lobby.level")}</span>
            <span className="block h-1.5 overflow-hidden rounded-full bg-muted">
              <span ref={meter} className="block h-full origin-left scale-x-0 rounded-full bg-success transition-transform duration-75" />
            </span>
          </div>
          <Field label={t("lobby.camera")}>
            <NativeSelect value={videoId} onChange={(e) => setVideoId(e.target.value)} disabled={cams.length === 0 || !camOn}>
              <option value="">{t("lobby.defaultDevice")}</option>
              {cams.map((d, i) => (
                <option key={d.deviceId || i} value={d.deviceId}>
                  {d.label || t("lobby.deviceNumber", { number: i + 1 })}
                </option>
              ))}
            </NativeSelect>
          </Field>
          {(micError === "denied" || camError === "denied") && (
            <p role="status" className="rounded-lg border border-warning/30 bg-warning/10 px-3 py-2 text-sm">
              {t("lobby.permissionDenied")}
            </p>
          )}
          {micError === "missing" && <p className="text-sm text-muted-foreground">{t("lobby.micMissing")}</p>}
          <button
            type="button"
            onClick={join}
            disabled={joining}
            className="mt-auto inline-flex h-11 items-center justify-center gap-2 rounded-lg bg-primary px-5 font-medium text-primary-foreground shadow-xs transition-colors hover:bg-primary/90 disabled:opacity-60"
          >
            {joining ? <Loader2 className="size-4 animate-spin" /> : <DoorOpen className="size-4" />}
            {t("lobby.join")}
          </button>
          <p className="text-xs text-muted-foreground">{t("lobby.limit")}</p>
        </div>
      </div>
    </main>
  );
}

// ---- room ----------------------------------------------------------------------------------------------------------

type Tile = { key: string; name: string; role?: Role; state: MediaState; stream: MediaStream | null; self: boolean; connecting: boolean };

function Room({ client, home, selfName }: { client: RoomClient; home: string; selfName: string }) {
  const t = useTranslations("room");
  const snap = useSyncExternalStore(client.subscribe, client.getSnapshot, client.getSnapshot);
  const [chatOpen, setChatOpen] = useState(false);
  const [seen, setSeen] = useState(0);
  const [notice, setNotice] = useState<string | null>(null);

  // Closing or reloading the tab leaves the room (frees the seat at once instead of after the ping timeout).
  useEffect(() => {
    const leave = () => client.leave();
    window.addEventListener("pagehide", leave);
    return () => window.removeEventListener("pagehide", leave);
  }, [client]);

  if (snap.phase === "ended" || snap.phase === "failed" || snap.phase === "left") {
    return <Finished snap={snap} home={home} />;
  }

  const tiles: Tile[] = [
    { key: "self", name: selfName, role: snap.role, state: snap.local, stream: snap.localStream, self: true, connecting: false },
    ...snap.peers.map((p) => ({
      key: p.id,
      name: p.name,
      role: p.role,
      state: p.state,
      stream: p.stream,
      self: false,
      connecting: p.connection === "new" || p.connection === "connecting",
    })),
  ];
  const spotlight = tiles.find((tile) => tile.state.screen && !tile.self) ?? tiles.find((tile) => tile.state.screen);
  const others = spotlight ? tiles.filter((tile) => tile !== spotlight) : tiles;
  const received = snap.chat.filter((m) => !m.self).length;
  const unread = chatOpen ? 0 : Math.max(0, received - seen);

  async function run(action: () => Promise<void>) {
    try {
      setNotice(null);
      await action();
    } catch {
      setNotice(t("errors.media"));
    }
  }

  function toggleChat() {
    setSeen(received);
    setChatOpen(!chatOpen);
  }

  function end() {
    if (window.confirm(t("controls.endConfirm"))) client.endForAll();
  }

  return (
    <div className="flex h-full flex-col">
      <header className="flex items-center justify-between gap-3 border-b px-4 py-2.5">
        <h1 className="truncate text-sm font-semibold">{t("title")}</h1>
        <div className="flex items-center gap-3 text-sm text-muted-foreground">
          {snap.phase !== "live" && (
            <span role="status" className="inline-flex items-center gap-1.5 rounded-full bg-warning/15 px-2.5 py-0.5 text-xs font-medium text-foreground">
              {snap.phase === "reconnecting" ? <WifiOff className="size-3.5" /> : <Loader2 className="size-3.5 animate-spin" />}
              {snap.phase === "reconnecting" ? t("status.reconnecting") : t("status.connecting")}
            </span>
          )}
          <span className="inline-flex items-center gap-1.5" title={t("status.participantsHint")}>
            <Users className="size-4" />
            {t("status.participants", { count: tiles.length, capacity: snap.capacity ?? tiles.length })}
          </span>
        </div>
      </header>

      <div className="relative flex min-h-0 flex-1">
        <main className="min-h-0 flex-1 overflow-y-auto p-3 sm:overflow-hidden sm:p-4">
          {spotlight ? (
            <div className="flex h-full flex-col gap-3 lg:flex-row">
              <div className="min-h-0 flex-1">
                <TileView tile={spotlight} large />
              </div>
              <div className="flex shrink-0 gap-3 overflow-x-auto lg:w-60 lg:flex-col lg:overflow-y-auto">
                {others.map((tile) => (
                  <div key={tile.key} className="w-44 shrink-0 lg:w-full">
                    <TileView tile={tile} />
                  </div>
                ))}
              </div>
            </div>
          ) : (
            // Phones: one column, scroll. Larger screens: every tile fits the visible area (rows share the height).
            <div
              className={cn("grid gap-3 sm:h-full sm:grid-rows-[repeat(var(--rows),minmax(0,1fr))]", gridColumns(tiles.length))}
              style={{ "--rows": gridRows(tiles.length) } as CSSProperties}
            >
              {tiles.map((tile) => (
                <TileView key={tile.key} tile={tile} fill />
              ))}
            </div>
          )}
        </main>
        {chatOpen && <ChatPanel messages={snap.chat} onSend={(text) => client.sendChat(text, selfName)} onClose={toggleChat} />}
      </div>

      {notice && (
        <p role="alert" className="mx-auto mb-2 rounded-lg bg-destructive/10 px-3 py-1.5 text-sm text-destructive">
          {notice}
        </p>
      )}
      <footer className="flex flex-wrap items-center justify-center gap-2.5 border-t px-3 py-3 sm:gap-3">
        <RoundButton on={snap.local.audio} onClick={() => run(() => client.setMic(!snap.local.audio))} label={snap.local.audio ? t("controls.micOn") : t("controls.micOff")}>
          {snap.local.audio ? <Mic className="size-5" /> : <MicOff className="size-5" />}
        </RoundButton>
        <RoundButton on={snap.local.video} onClick={() => run(() => client.setCamera(!snap.local.video))} label={snap.local.video ? t("controls.camOn") : t("controls.camOff")}>
          {snap.local.video ? <Video className="size-5" /> : <VideoOff className="size-5" />}
        </RoundButton>
        <RoundButton
          on={!snap.local.screen}
          active={snap.local.screen}
          onClick={() => run(() => client.setScreen(!snap.local.screen))}
          label={snap.local.screen ? t("controls.stopShare") : t("controls.share")}
          className="hidden sm:inline-flex"
        >
          <MonitorUp className="size-5" />
        </RoundButton>
        <RoundButton on active={chatOpen} onClick={toggleChat} label={t("controls.chat")} badge={unread}>
          <MessageSquare className="size-5" />
        </RoundButton>
        <button
          type="button"
          onClick={() => client.leave()}
          className="inline-flex h-11 items-center gap-2 rounded-full bg-destructive px-4 text-sm font-medium text-white transition-colors hover:bg-destructive/90"
        >
          <PhoneOff className="size-4.5" />
          {t("controls.leave")}
        </button>
        {snap.role === "host" && (
          <button
            type="button"
            onClick={end}
            className="inline-flex h-11 items-center gap-2 rounded-full border border-destructive/40 px-4 text-sm font-medium text-destructive transition-colors hover:bg-destructive/10"
          >
            {t("controls.end")}
          </button>
        )}
      </footer>
    </div>
  );
}

function gridColumns(count: number): string {
  if (count <= 1) return "grid-cols-1";
  if (count <= 4) return "grid-cols-1 sm:grid-cols-2";
  if (count <= 6) return "grid-cols-1 sm:grid-cols-3";
  return "grid-cols-2 sm:grid-cols-4";
}

function gridRows(count: number): number {
  if (count <= 2) return 1;
  if (count <= 6) return 2;
  return Math.ceil(count / 4);
}

function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(-2)
    .map((part) => part[0]?.toUpperCase())
    .join("");
}

function TileView({ tile, large, fill }: { tile: Tile; large?: boolean; fill?: boolean }) {
  const t = useTranslations("room");
  const showVideo = !!tile.stream && (tile.state.video || tile.state.screen);
  return (
    <figure
      className={cn(
        "relative overflow-hidden rounded-xl border bg-muted",
        large ? "size-full min-h-56" : fill ? "aspect-video sm:aspect-auto sm:h-full sm:min-h-0" : "aspect-video",
      )}
    >
      {/* Remote audio plays through this element even when the camera is off. */}
      {tile.stream && (
        <VideoView
          stream={tile.stream}
          muted={tile.self}
          mirror={tile.self && !tile.state.screen}
          className={cn("size-full", tile.state.screen ? "object-contain" : "object-cover", !showVideo && "invisible")}
        />
      )}
      {!showVideo && (
        <div className="absolute inset-0 grid place-items-center">
          <span className="grid size-16 place-items-center rounded-full bg-primary/15 text-xl font-semibold text-primary sm:size-20 sm:text-2xl">
            {initials(tile.name)}
          </span>
        </div>
      )}
      <figcaption className="absolute inset-x-2 bottom-2 flex items-center gap-1.5">
        <span className="inline-flex max-w-full items-center gap-1.5 truncate rounded-md bg-black/55 px-2 py-0.5 text-xs font-medium text-white">
          {!tile.state.audio && <MicOff className="size-3.5 shrink-0" aria-label={t("status.muted")} />}
          <span className="truncate">{tile.self ? t("status.you", { name: tile.name }) : tile.name}</span>
          {tile.role === "host" && <span className="shrink-0 text-white/70">{t("status.host")}</span>}
        </span>
        {tile.state.screen && (
          <span className="inline-flex items-center gap-1 rounded-md bg-primary px-2 py-0.5 text-xs font-medium text-primary-foreground">
            <MonitorUp className="size-3.5" />
            {t("status.screen")}
          </span>
        )}
      </figcaption>
      {tile.connecting && (
        <span className="absolute top-2 right-2 inline-flex items-center gap-1 rounded-md bg-black/55 px-2 py-0.5 text-xs text-white">
          <Loader2 className="size-3.5 animate-spin" />
          {t("status.peerConnecting")}
        </span>
      )}
    </figure>
  );
}

function VideoView({ stream, muted, mirror, className }: { stream: MediaStream; muted?: boolean; mirror?: boolean; className?: string }) {
  const ref = useRef<HTMLVideoElement>(null);
  useEffect(() => {
    const video = ref.current;
    if (video && video.srcObject !== stream) {
      video.srcObject = stream;
      video.play().catch(() => undefined);
    }
  }, [stream]);
  return <video ref={ref} autoPlay playsInline muted={muted} className={cn(mirror && "-scale-x-100", className)} />;
}

function RoundButton({ on, active, disabled, onClick, label, badge, className, children }: {
  on: boolean;
  active?: boolean;
  disabled?: boolean;
  onClick: () => void;
  label: string;
  badge?: number;
  className?: string;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      title={label}
      aria-pressed={active ?? on}
      className={cn(
        "relative inline-flex size-11 items-center justify-center rounded-full border transition-colors disabled:opacity-50",
        active
          ? "border-primary bg-primary text-primary-foreground hover:bg-primary/90"
          : on
            ? "bg-background text-foreground hover:bg-accent"
            : "border-destructive bg-destructive text-white hover:bg-destructive/90",
        className,
      )}
    >
      {children}
      {!!badge && (
        <span className="absolute -top-1 -right-1 grid min-w-5 place-items-center rounded-full bg-destructive px-1 text-[11px] font-semibold text-white">
          {badge}
        </span>
      )}
    </button>
  );
}

function ChatPanel({ messages, onSend, onClose }: { messages: ChatMessage[]; onSend: (text: string) => void; onClose: () => void }) {
  const t = useTranslations("room");
  const format = useFormatter();
  const [text, setText] = useState("");
  const list = useRef<HTMLOListElement>(null);

  useEffect(() => {
    list.current?.lastElementChild?.scrollIntoView({ block: "end" });
  }, [messages.length]);

  function submit(event: FormEvent) {
    event.preventDefault();
    onSend(text);
    setText("");
  }

  return (
    <aside className="absolute inset-0 z-20 flex flex-col bg-background md:static md:w-80 md:border-l">
      <div className="flex items-center justify-between border-b px-4 py-2.5">
        <h2 className="text-sm font-semibold">{t("chat.title")}</h2>
        <button type="button" onClick={onClose} aria-label={t("chat.close")} className="rounded-md p-1 text-muted-foreground hover:bg-accent">
          <X className="size-4" />
        </button>
      </div>
      {messages.length === 0 ? (
        <p className="p-4 text-sm text-muted-foreground">{t("chat.empty")}</p>
      ) : (
        <ol ref={list} className="min-h-0 flex-1 space-y-3 overflow-y-auto p-4">
          {messages.map((m) => (
            <li key={m.id} className={cn("flex flex-col gap-0.5", m.self && "items-end")}>
              <span className="text-xs text-muted-foreground">
                {m.self ? t("chat.me") : m.name}
                {" · "}
                {format.dateTime(new Date(m.at), { hour: "2-digit", minute: "2-digit" })}
              </span>
              <span className={cn("max-w-[85%] rounded-xl px-3 py-1.5 text-sm break-words whitespace-pre-wrap", m.self ? "bg-primary text-primary-foreground" : "bg-muted")}>
                {m.text}
              </span>
            </li>
          ))}
        </ol>
      )}
      <form onSubmit={submit} className="mt-auto flex gap-2 border-t p-3">
        <input
          value={text}
          onChange={(e) => setText(e.target.value)}
          maxLength={1000}
          placeholder={t("chat.placeholder")}
          aria-label={t("chat.placeholder")}
          className="h-9 min-w-0 flex-1 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 dark:bg-input/30"
        />
        <button type="submit" disabled={!text.trim()} aria-label={t("chat.send")} className="inline-flex size-9 items-center justify-center rounded-md bg-primary text-primary-foreground disabled:opacity-50">
          <Send className="size-4" />
        </button>
      </form>
    </aside>
  );
}

// ---- end screens ---------------------------------------------------------------------------------------------------

function errorKey(code: string | undefined): string {
  const key = (code ?? "unknown").replaceAll(".", "_");
  return ["room_full", "replaced", "workspace_room_not_found", "workspace_room_not_configured", "session_expired"].includes(key) ? key : "unknown";
}

function Finished({ snap, home }: { snap: Snapshot; home: string }) {
  const t = useTranslations("room");
  const ended = snap.phase === "ended";
  const title = ended ? t("ended.title") : snap.phase === "left" ? t("left.title") : t("errors.title");
  const body = ended ? t("ended.body") : snap.phase === "left" ? t("left.body") : t(`errors.${errorKey(snap.error)}` as "errors.unknown");
  return (
    <main className="mx-auto flex h-full max-w-md flex-col items-center justify-center gap-4 px-4 text-center">
      <span className={cn("grid size-16 place-items-center rounded-2xl", ended ? "bg-success/15 text-success" : "bg-primary/10 text-primary")}>
        {ended ? <CircleCheck className="size-8" /> : <DoorOpen className="size-8" />}
      </span>
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      <p className="text-muted-foreground">{body}</p>
      <div className="flex flex-wrap justify-center gap-3">
        {ended && snap.role === "host" && snap.sessionId && (
          <a href={`/teach/sessions/${snap.sessionId}/attendance`} className="inline-flex h-10 items-center rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground hover:bg-primary/90">
            {t("ended.attendance")}
          </a>
        )}
        {!ended && snap.error !== "room_full" && snap.error !== "workspace.room_not_found" && (
          <button type="button" onClick={() => window.location.reload()} className="inline-flex h-10 items-center rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground hover:bg-primary/90">
            {t("rejoin")}
          </button>
        )}
        <a href={home} className="inline-flex h-10 items-center rounded-lg border px-4 text-sm font-medium hover:bg-accent">
          {t("home")}
        </a>
      </div>
    </main>
  );
}
