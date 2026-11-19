using System.Reflection;
namespace BackupControl.Agent;
public static class AgentVersion
{
 public static string Current=>typeof(AgentVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]??typeof(AgentVersion).Assembly.GetName().Version!.ToString(3);
 public static bool RemoteUpdateSupported=>File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"BackupControlAgentUpdater","enabled-v1.txt"));
}
