using System.Diagnostics;
using System.Text.Json;

namespace BackupControl.Agent;

public sealed record MaintenanceRequest(int OwnerPid, long OwnerStartUtcTicks, Guid Nonce);
public sealed record MaintenanceReady(MaintenanceRequest Request, int WorkerPid, long WorkerStartUtcTicks);

public static class MaintenanceGate
{
    private static readonly Dictionary<string, (MaintenanceRequest Request, FileStream Lease)> Owned =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly long StartTicks = GetStartTicks();
    private const string RequestFile = "maintenance.request";
    private const string ReadyFile = "maintenance.ready";

    private static long GetStartTicks()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    public static bool Requested(string data) => File.Exists(Path.Combine(data, RequestFile));

    // Both processes serialize replacement/removal so a stale-owner cleanup cannot remove a newer request.
    private static FileStream Guard(string data)
    {
        var start = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.Combine(data, "maintenance.guard"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when (SharingViolation(ex) && start.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(25); }
        }
    }

    private static bool SharingViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33;

    public static void Begin(string data)
    {
        using var guard = Guard(data);
        lock (Owned)
        {
            if (!Owned.TryGetValue(data, out var owner))
            {
                // An exclusive handle cannot survive owner termination. Checking it also works
                // when a virtual service account cannot inspect the elevated installer's process.
                owner = (new MaintenanceRequest(Environment.ProcessId, StartTicks, Guid.NewGuid()),
                    new FileStream(Path.Combine(data, "maintenance.owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            var request = new MaintenanceRequest(Environment.ProcessId, StartTicks, Guid.NewGuid());
            try
            {
                Write(data, RequestFile, request);
                Owned[data] = (request, owner.Lease);
            }
            catch
            {
                if (!Owned.ContainsKey(data)) owner.Lease.Dispose();
                throw;
            }
        }
    }

    public static void End(string data)
    {
        lock (Owned) { if (!Owned.ContainsKey(data)) return; }
        using var guard = Guard(data);
        lock (Owned)
        {
            if (!Owned.TryGetValue(data, out var owner)) return;
            try
            {
                File.Delete(Path.Combine(data, RequestFile));
                File.Delete(Path.Combine(data, ReadyFile));
            }
            finally { Owned.Remove(data); owner.Lease.Dispose(); }
        }
    }

    public static bool IsReady(string data, int workerPid, long workerStartUtcTicks)
    {
        using var guard = Guard(data);
        lock (Owned)
        {
            if (!Owned.TryGetValue(data, out var owner)) return false;
            var ack = Read<MaintenanceReady>(data, ReadyFile);
            return ack?.Request == owner.Request && ack.WorkerPid == workerPid && ack.WorkerStartUtcTicks == workerStartUtcTicks;
        }
    }

    // Called only between cycles or by a one-shot process holding agent.lock.
    public static bool Check(string data, bool acknowledge)
    {
        if (!Requested(data)) return false;
        using var guard = Guard(data);
        if (!Requested(data)) return false;
        var request = Read<MaintenanceRequest>(data, RequestFile);
        if (request is null || request.Nonce == Guid.Empty)
            throw new InvalidDataException("Solicitação de manutenção incompatível. É necessário verificar a instalação.");
        try
        {
            using var abandoned = new FileStream(Path.Combine(data, "maintenance.owner"), FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException ex) when (SharingViolation(ex))
        {
            if (acknowledge)
            {
                var ack = new MaintenanceReady(request, Environment.ProcessId, StartTicks);
                if (Read<MaintenanceReady>(data, ReadyFile) != ack) Write(data, ReadyFile, ack);
            }
            return true;
        }
        File.Delete(Path.Combine(data, RequestFile));
        File.Delete(Path.Combine(data, ReadyFile));
        return false;
    }

    private static T? Read<T>(string data, string name) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(data, name))); }
        catch (FileNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }

    private static void Write<T>(string data, string name, T value)
    {
        var target = Path.Combine(data, name);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value));
            File.Move(temporary, target, true);
        }
        finally { File.Delete(temporary); }
    }
}
