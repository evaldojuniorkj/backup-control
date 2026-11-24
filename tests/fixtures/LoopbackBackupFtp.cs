using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Speaks only the FTP commands used by these integration tests. All files live in memory.
internal sealed class LoopbackBackupFtp : IAsyncDisposable
{
    private sealed record Entry(byte[] Content, DateTimeOffset Modified);
    private readonly TcpListener control = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource closed = new();
    private readonly ConcurrentDictionary<string, Entry> files = new(StringComparer.Ordinal);
    private readonly Task server;
    public int Port => ((IPEndPoint)control.LocalEndpoint).Port;
    public IEnumerable<string> Paths => files.Keys;
    public ConcurrentQueue<string> Deleted { get; } = new();
    public ConcurrentQueue<(string Command, int Bytes)> Transfers { get; } = new();
    public bool CorruptOnAppend { get; init; }

    public LoopbackBackupFtp()
    {
        control.Start();
        server = Serve();
    }

    public void Put(string path, byte[] bytes, DateTimeOffset modified) => files[path] = new(bytes, modified);
    public byte[] Get(string path) => files[path].Content;

    private async Task Serve()
    {
        try
        {
            while (!closed.IsCancellationRequested)
            {
                using var client = await control.AcceptTcpClientAsync(closed.Token);
                await Handle(client.GetStream(), closed.Token);
            }
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
    }

    private async Task Handle(NetworkStream stream, CancellationToken ct)
    {
        using var input = new StreamReader(stream, leaveOpen: true);
        await using var output = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            { AutoFlush = true, NewLine = "\r\n" };
        var passive = new TcpListener(IPAddress.Loopback, 0);
        var currentDirectory = "/";
        string? renameFrom = null;
        async Task Send(byte[] data)
        {
            await output.WriteLineAsync("150 Data connection");
            using (var connection = await passive.AcceptTcpClientAsync(ct))
                await connection.GetStream().WriteAsync(data, ct);
            passive.Stop();
            await output.WriteLineAsync("226 Transfer complete");
        }

        await output.WriteLineAsync("220 Backup test fixture");
        try
        {
            while (await input.ReadLineAsync(ct) is { } line)
            {
                var fields = line.Split(' ', 2);
                var command = fields[0];
                var path = fields.Length == 2 ? fields[1] : "/";
                switch (command)
                {
                    case "USER": await output.WriteLineAsync("331 Password required"); break;
                    case "PASS": await output.WriteLineAsync("230 Logged in"); break;
                    case "FEAT": await output.WriteLineAsync("211-Features\r\n MLST type*;size*;modify*;\r\n MDTM\r\n SIZE\r\n UTF8\r\n211 End"); break;
                    case "SYST": await output.WriteLineAsync("215 UNIX Type: L8"); break;
                    case "PWD": await output.WriteLineAsync($"257 \"{currentDirectory}\""); break;
                    case "CWD":
                        currentDirectory = path;
                        await output.WriteLineAsync("250 Directory changed");
                        break;
                    case "MKD": await output.WriteLineAsync($"257 \"{path}\""); break;
                    case "QUIT": await output.WriteLineAsync("221 Goodbye"); return;
                    case "TYPE":
                    case "OPTS":
                    case "NOOP": await output.WriteLineAsync("200 OK"); break;
                    case "EPSV":
                        passive.Start();
                        await output.WriteLineAsync($"229 Entering Extended Passive Mode (|||{((IPEndPoint)passive.LocalEndpoint).Port}|)");
                        break;
                    case "MLSD": await Send(Encoding.UTF8.GetBytes(Listing(path))); break;
                    case "RETR":
                        if (files.TryGetValue(path, out var file)) await Send(file.Content);
                        else await output.WriteLineAsync("550 File unavailable");
                        break;
                    case "STOR":
                    case "APPE":
                        await output.WriteLineAsync("150 Data connection");
                        using (var destination = new MemoryStream())
                        {
                            using (var connection = await passive.AcceptTcpClientAsync(ct))
                                await connection.GetStream().CopyToAsync(destination, ct);
                            var bytes = destination.ToArray();
                            Transfers.Enqueue((command, bytes.Length));
                            if (command == "APPE" && files.TryGetValue(path, out var partial))
                            {
                                bytes = [..partial.Content, ..bytes];
                                if (CorruptOnAppend && bytes.Length > 0) bytes[0] ^= 0xff;
                            }
                            Put(path, bytes, DateTimeOffset.UtcNow);
                        }
                        passive.Stop();
                        await output.WriteLineAsync("226 Transfer complete");
                        break;
                    case "RNFR":
                        renameFrom = files.ContainsKey(path) ? path : null;
                        await output.WriteLineAsync(renameFrom is null ? "550 File unavailable" : "350 Ready to rename");
                        break;
                    case "RNTO":
                        if (renameFrom is not null && files.TryRemove(renameFrom, out var renamed))
                        {
                            files[path] = renamed;
                            await output.WriteLineAsync("250 File renamed");
                        }
                        else await output.WriteLineAsync("550 File unavailable");
                        renameFrom = null;
                        break;
                    case "SIZE":
                        await output.WriteLineAsync(files.TryGetValue(path, out var sized) ? $"213 {sized.Content.Length}" : "550 File unavailable");
                        break;
                    case "MDTM":
                        await output.WriteLineAsync(files.TryGetValue(path, out var dated) ? $"213 {Timestamp(dated.Modified)}" : "550 File unavailable");
                        break;
                    case "DELE":
                        if (files.TryRemove(path, out _))
                        {
                            Deleted.Enqueue(path);
                            await output.WriteLineAsync("250 File deleted");
                        }
                        else await output.WriteLineAsync("550 File unavailable");
                        break;
                    default: await output.WriteLineAsync("502 Unsupported command"); break;
                }
            }
        }
        finally { passive.Stop(); }
    }

    private string Listing(string path)
    {
        var prefix = path.TrimEnd('/') + "/";
        var entries = files.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(pair =>
            {
                var child = pair.Key[prefix.Length..];
                var slash = child.IndexOf('/');
                return slash < 0
                    ? $"type=file;size={pair.Value.Content.Length};modify={Timestamp(pair.Value.Modified)}; {child}\r\n"
                    : $"type=dir; {child[..slash]}\r\n";
            }).Distinct(StringComparer.Ordinal);
        return string.Concat(entries);
    }

    private static string Timestamp(DateTimeOffset modified) =>
        modified.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await closed.CancelAsync();
        control.Stop();
        await server;
        closed.Dispose();
    }
}
