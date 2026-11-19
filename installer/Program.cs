using BackupControl.Agent;
using Microsoft.Data.SqlClient;
using System.Security.Principal;
using System.Text.Json;
namespace BackupControl.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if(args.Contains("--apply-pending-update",StringComparer.Ordinal)){Environment.ExitCode=args.Length==1?RemoteUpdater.Run(CancellationToken.None).GetAwaiter().GetResult():5;return;}
        ApplicationConfiguration.Initialize();
        BootstrapConfig? config=null;GenericBootstrap? generic=null;string? error=null;InstallLocations locations=InstallLocations.Default();
        try {
            locations=SetupArguments.Resolve(args);
            using var executable=File.OpenRead(Environment.ProcessPath??throw new SetupException("Não foi possível identificar o executável."));if(GenericBootstrap.IsGeneric(executable))generic=GenericBootstrap.Read(executable);else config=BootstrapConfig.Read(executable);
        }catch(Exception e){config=null;error=Friendly(e);}
        if(generic is not null&&error is null)Application.Run(new GenericSetupWindow(generic,locations));else Application.Run(new SetupWindow(config,locations,error));
    }
    internal static string Friendly(Exception e)=>e switch{
        SetupException=>e.Message,
        SqlException sql=>sql.Number switch{18456=>"O SQL Server recusou o usuário ou a senha. Corrija a conexão SQL compartilhada no painel e tente novamente neste instalador.",-2146893019=>"O certificado do SQL Server não é confiável. Confira o certificado e a opção de confiança no painel; depois tente novamente.",233=>"O SQL Server encerrou a conexão durante o acesso (erro 233). Verifique a instância, a autenticação SQL e a negociação TLS no computador do cliente.",_=>$"SQL Server retornou o erro {sql.Number}. Verifique instância local, banco ONLINE e permissões autorizadas."},
        HttpRequestException=>"Não foi possível alcançar a central por HTTPS. Confira a internet e a URL configurada no painel; mantenha a central e o túnel ativos.",
        TaskCanceledException=>"A conexão excedeu o tempo de espera. Verifique a internet e tente novamente.",
        UnauthorizedAccessException=>"O Windows negou acesso. Execute o instalador como administrador e confira as permissões da pasta.",
        AgentUnauthorizedException=>"O cliente está desativado ou a credencial foi recusada. Confira o cadastro no painel.",
        _=>"Não foi possível concluir esta etapa ("+e.GetType().Name+"). Verifique a configuração no painel e tente novamente. Nenhuma senha é exibida neste diagnóstico."
    };
}

internal sealed class SetupWindow:Form
{
    public bool InstallSucceeded {get;private set;}
    private readonly BootstrapConfig? config;private readonly InstallLocations locations;private readonly Guid? approvedInstance;private readonly ReinstallationApproval? reinstallation;
    private readonly Label heading=new(),description=new(),status=new();
    private readonly ListBox steps=new();private readonly TextBox detail=new();private readonly ProgressBar progress=new();private readonly Button retry=new(),close=new();
    private bool busy;
    public SetupWindow(BootstrapConfig? config,InstallLocations locations,string? error,Guid? approvedInstance=null,ReinstallationApproval? reinstallation=null)
    {
        this.config=config;this.locations=locations;this.approvedInstance=approvedInstance;this.reinstallation=reinstallation;
        Text="Backup Control — Instalação automática";Size=new(800,590);MinimumSize=new(680,520);StartPosition=FormStartPosition.CenterScreen;Font=new("Segoe UI",10);BackColor=Color.FromArgb(245,248,246);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(28),ColumnCount=1,RowCount=7};
        layout.RowStyles.Add(new(SizeType.Absolute,48));layout.RowStyles.Add(new(SizeType.Absolute,54));layout.RowStyles.Add(new(SizeType.Absolute,30));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,88));layout.RowStyles.Add(new(SizeType.Absolute,30));layout.RowStyles.Add(new(SizeType.Absolute,48));
        heading.Text=config is null?"Instalador do agente Windows":config.ClientName;heading.Font=new("Segoe UI",20,FontStyle.Bold);heading.ForeColor=Color.FromArgb(25,65,46);heading.Dock=DockStyle.Fill;
        description.Text="A configuração será recebida da central. Não é necessário informar URL, token ou dados SQL nesta máquina.";description.Dock=DockStyle.Fill;
        progress.Dock=DockStyle.Fill;progress.Style=ProgressBarStyle.Marquee;progress.MarqueeAnimationSpeed=25;
        steps.Dock=DockStyle.Fill;steps.BorderStyle=BorderStyle.FixedSingle;steps.BackColor=Color.White;steps.HorizontalScrollbar=true;steps.Font=new("Segoe UI",10);
        detail.Dock=DockStyle.Fill;detail.Multiline=true;detail.ReadOnly=true;detail.BorderStyle=BorderStyle.None;detail.BackColor=BackColor;detail.ForeColor=Color.FromArgb(153,55,43);detail.Visible=false;
        status.Dock=DockStyle.Fill;status.Font=new("Segoe UI",9);status.ForeColor=Color.FromArgb(75,95,80);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};close.Text="Fechar";close.Width=110;close.Height=36;close.Click+=(_,_)=>Close();retry.Text="Tentar novamente";retry.Width=160;retry.Height=36;retry.Visible=false;retry.Click+=async(_,_)=>await RunInstall();actions.Controls.Add(close);actions.Controls.Add(retry);
        layout.Controls.Add(heading,0,0);layout.Controls.Add(description,0,1);layout.Controls.Add(progress,0,2);layout.Controls.Add(steps,0,3);layout.Controls.Add(detail,0,4);layout.Controls.Add(status,0,5);layout.Controls.Add(actions,0,6);Controls.Add(layout);
        FormClosing+=(_,e)=>{if(busy){e.Cancel=true;status.Text="Aguarde a conclusão para fechar sem interromper a configuração.";}};
        Shown+=async(_,_)=>{if(error is not null){ShowError(error);return;}await RunInstall();};
    }
    private async Task RunInstall()
    {
        if(config is null)return;
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)){ShowError("Autorize a execução como administrador do Windows para instalar o serviço.");return;}
        busy=true;close.Enabled=false;retry.Visible=false;detail.Visible=false;progress.Style=ProgressBarStyle.Marquee;steps.Items.Clear();status.Text="Instalação em andamento…";
        var reporter=new Progress<string>(message=>{steps.Items.Add(message);steps.TopIndex=Math.Max(0,steps.Items.Count-1);status.Text=message;});
        try {
            var outcome=await new InstallerEngine(config,locations,reporter,approvedInstance,reinstallation).Install(CancellationToken.None);
            InstallSucceeded=true;
            progress.Style=ProgressBarStyle.Continuous;progress.Value=100;heading.Text="Agente instalado e conectado";
            description.Text=outcome.ClientName+" · "+outcome.MachineName;
            detail.ForeColor=Color.FromArgb(30,100,63);detail.Text="Central: aprovada. SQL: aprovado. FTP: aprovado. Serviço: em execução.\r\nA rotina cadastrada já está ativa. "+(outcome.DifferentialEnabled?"Sem uma base conhecida, o primeiro completo pode iniciar automaticamente.":"Solicite o primeiro completo pelo painel para validar o backup real.");detail.Visible=true;
            status.Text="Você já pode acompanhar esta máquina no painel.";SaveResult(true,null);
        }catch(Exception e){ShowError(Program.Friendly(e));SaveResult(false,Program.Friendly(e));}
        finally{busy=false;close.Enabled=true;if(locations.TestMode&&!IsDisposed)BeginInvoke((Action)(()=>Close()));}
    }
    private void ShowError(string message)
    {
        progress.Style=ProgressBarStyle.Continuous;progress.Value=0;detail.ForeColor=Color.FromArgb(153,55,43);detail.Text=message;detail.Visible=true;status.Text="Instalação não concluída. Revise a etapa indicada antes de repetir.";retry.Visible=config is not null;
    }
    private void SaveResult(bool success,string? error)
    {
        if(!locations.TestMode)return;
        try{File.WriteAllText(Path.Combine(Path.GetDirectoryName(locations.Data)!,"result.json"),JsonSerializer.Serialize(new{success,error,clientId=config?.ClientId,machineName=Environment.MachineName,finishedAt=DateTimeOffset.UtcNow}));}catch{ /* UI remains authoritative if a test report cannot be written. */ }
    }
}

