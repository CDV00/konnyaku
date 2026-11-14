namespace Konnyaku.SharedKernel;

/// <summary>
/// Event crossing a module boundary. Published through the module's outbox; type names carry a version suffix (V1, V2...).
/// </summary>
public interface IIntegrationEvent;
