namespace BackupControl.Agent;

public sealed class InstanceLease : IDisposable
{
    private readonly Semaphore semaphore;
    public InstanceLease(Guid instanceId)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("Reconfigure o agente para gerar sua identificação de instalação.");
        semaphore = new Semaphore(1, 1, @"Global\BackupControlAgent-" + instanceId.ToString("N"));
        if (!semaphore.WaitOne(0)) { semaphore.Dispose(); throw new IOException("Esta instalação já está em execução em outro processo."); }
    }
    public void Dispose() { semaphore.Release(); semaphore.Dispose(); }
}
