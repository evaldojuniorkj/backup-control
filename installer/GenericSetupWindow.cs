using BackupControl.Agent;
using System.Security.Principal;
using System.Text.Json;
namespace BackupControl.Setup;

internal sealed class GenericSetupWindow:Form
{
    private readonly GenericBootstrap bootstrap;private readonly InstallLocations locations;
    private readonly TextBox name=new(),machine=new(),code=new();private readonly Label status=new(),description=new();
    private readonly Button submit=new(),close=new();private readonly CancellationTokenSource cancellation=new();
    private ReinstallationApproval? reinstallation;private bool reinstallConfirmed;private DeviceRegistration? registration;private LocalSettings? previous;private Guid instance;private bool polling;private bool installing;private Mutex? registrationMutex;private bool ownsMutex;
    public GenericSetupWindow(GenericBootstrap bootstrap,InstallLocations locations)
    {
        this.bootstrap=bootstrap;this.locations=locations;
        Text="Backup Control — Cadastro da máquina";Size=new(740,590);MinimumSize=new(680,560);StartPosition=FormStartPosition.CenterScreen;Font=new("Segoe UI",10);BackColor=Color.FromArgb(245,248,246);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(30),ColumnCount=1,RowCount=10};
        layout.RowStyles.Add(new(SizeType.Absolute,48));layout.RowStyles.Add(new(SizeType.Absolute,64));layout.RowStyles.Add(new(SizeType.Absolute,26));layout.RowStyles.Add(new(SizeType.Absolute,36));layout.RowStyles.Add(new(SizeType.Absolute,26));layout.RowStyles.Add(new(SizeType.Absolute,36));layout.RowStyles.Add(new(SizeType.Absolute,64));layout.RowStyles.Add(new(SizeType.Absolute,60));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,48));
        var title=new Label{Text="Cadastrar esta máquina",Font=new("Segoe UI",21,FontStyle.Bold),ForeColor=Color.FromArgb(25,65,46),Dock=DockStyle.Fill};
        description.Text="Informe o nome do cliente. A equipe deverá aprovar o código no painel antes de liberar a instalação e as configurações.";description.Dock=DockStyle.Fill;
        name.Text=locations.TestMode?"QA genérico — "+Environment.MachineName:Environment.MachineName;name.MaxLength=120;name.Dock=DockStyle.Fill;name.AccessibleName="Nome do cliente";
        machine.Text=Environment.MachineName;machine.ReadOnly=true;machine.Dock=DockStyle.Fill;machine.AccessibleName="Nome da máquina";
        code.ReadOnly=true;code.Text="Código disponível após solicitar cadastro";code.Font=new("Consolas",17,FontStyle.Bold);code.Dock=DockStyle.Fill;code.AccessibleName="Código de aprovação";
        status.Dock=DockStyle.Fill;status.Text="Central: "+bootstrap.CentralUrl;status.ForeColor=Color.FromArgb(60,85,70);
        var note=new Label{Text="As credenciais SQL e FTP serão recebidas pela API. Nunca informe a senha administrativa do painel nesta máquina.",Dock=DockStyle.Fill};
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};close.Text="Fechar";close.Width=110;close.Height=36;close.Click+=(_,_)=>Close();submit.Text="Solicitar cadastro";submit.Width=190;submit.Height=36;submit.Click+=async(_,_)=>await Start();actions.Controls.Add(close);actions.Controls.Add(submit);
        layout.Controls.Add(title,0,0);layout.Controls.Add(description,0,1);layout.Controls.Add(new Label{Text="Nome do cliente",Dock=DockStyle.Fill},0,2);layout.Controls.Add(name,0,3);layout.Controls.Add(new Label{Text="Computador identificado",Dock=DockStyle.Fill},0,4);layout.Controls.Add(machine,0,5);layout.Controls.Add(code,0,6);layout.Controls.Add(status,0,7);layout.Controls.Add(note,0,8);layout.Controls.Add(actions,0,9);Controls.Add(layout);
        FormClosing+=(_,e)=>{if(installing){e.Cancel=true;return;}cancellation.Cancel();};
        Shown+=async(_,_)=>{try{var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(locations.Data).ToUpperInvariant())));registrationMutex=new Mutex(false,@"Global\BackupControlRegistration-"+hash);try{ownsMutex=registrationMutex.WaitOne(0);}catch(AbandonedMutexException){ownsMutex=true;}if(!ownsMutex)throw new SetupException("Já existe uma janela de cadastro aberta para esta instalação. Use a primeira janela.");await Prepare();if((locations.TestMode||registration is not null)&&reinstallation is null)await Start();}catch(Exception e){Failure(e);submit.Enabled=false;}};
        FormClosed+=(_,_)=>{if(ownsMutex)registrationMutex?.ReleaseMutex();registrationMutex?.Dispose();cancellation.Dispose();};
    }
    private async Task Prepare()
    {
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new SetupException("Execute o instalador como administrador para cadastrar e instalar o serviço.");
        PackageSafety.ValidateExistingTree(locations.Install);PackageSafety.ValidateExistingTree(locations.Data);
        previous=await new ProtectedStore(locations.Data).Load<LocalSettings>("settings",cancellation.Token);
        registration=new GenericRegistrationStore(locations.Data).Load();
        if(registration is not null){if(previous is not null&&previous.InstanceId!=registration.InstanceId)throw new SetupException("O cadastro local e o serviço possuem identidades diferentes. Solicite revisão da equipe.");name.Text=registration.RequestedName;}
        var journal=InstallerEngine.ReadJournal(locations.Data);
        instance=InstallerEngine.ResolveInstance(previous?.InstanceId,journal?.InstanceId,registration?.InstanceId);
        if(instance==Guid.Empty)throw new SetupException("A instalação antiga não possui identidade válida; é necessária atualização assistida.");
        if(previous is not null){
            var oldOrigin=BootstrapConfig.ValidateOrigin(previous.CentralUrl);
            if(oldOrigin!=bootstrap.CentralUrl||bootstrap.CentralUrl==ReinstallationPolicy.ProductionOrigin){
                var state=await new ProtectedStore(locations.Data).Read(cancellation.Token);
                if(state.Current is not null)throw new SetupException("Há backup em andamento ou aguardando retomada. Aguarde antes de reinstalar.");
                if(journal is not null&&state.Config is not null&&journal.ClientId!=state.Config.ClientId)throw new SetupException("As identidades do cliente divergem. Nada foi substituído.");
                reinstallation=ReinstallationPolicy.Plan(previous,bootstrap.CentralUrl,state.Config?.ClientId??journal?.ClientId??Guid.Empty);
                if(registration is not null&&registration.CentralUrl!=bootstrap.CentralUrl)registration=null;
                description.Text="Agente antigo encontrado. Reinstale para conectar diretamente à central definitiva, preservando o cadastro, os backups e os resultados pendentes. A equipe aprovará o mesmo cliente no painel.";
                submit.Text="Reinstalar agente";
                status.Text="Nova central: "+bootstrap.CentralUrl;
            }
            else submit.Text="Reinstalar agente";
        }
        if(registration is not null)InstallerEngine.ValidateRepairOrigin(registration.CentralUrl,bootstrap.CentralUrl);

    }
    private async Task Start()
    {
        if(polling)return;
        if(reinstallation is not null&&!reinstallConfirmed){
            var answer=MessageBox.Show(this,"Substituir o agente antigo e conectar esta máquina diretamente a "+reinstallation.TargetOrigin+"?\n\nA identidade instalada será usada para comprovar o mesmo cliente na nova central. Os backups e resultados serão preservados. A equipe ainda precisa aprovar o código no painel.","Reinstalar agente",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2);
            if(answer!=DialogResult.Yes)return;reinstallConfirmed=true;
        }
        polling=true;submit.Enabled=false;name.Enabled=false;status.ForeColor=Color.FromArgb(60,85,70);
        try{
            registration??=DeviceRegistration.Create(bootstrap.CentralUrl,name.Text,instance);
            new GenericRegistrationStore(locations.Data).Save(registration);
            using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};var client=new GenericEnrollmentClient(bootstrap.CentralUrl,http);
            status.Text="Registrando a solicitação na central…";
            var reply=await client.Resume(registration,previous?.AgentToken,cancellation.Token);
            while(true){
                cancellation.Token.ThrowIfCancellationRequested();if(IsDisposed)return;
                code.Text=reply.Code;
                if(locations.TestMode)WriteQa(reply);
                if(reply.Status=="Approved"){
                    var approved=GenericEnrollmentClient.ApprovedBootstrap(registration,reply);if(reinstallation is not null&&approved.ClientId!=reinstallation.ClientId)throw new SetupException("A aprovação deve usar o mesmo cliente da instalação antiga. Nada foi substituído.");cancellation.Token.ThrowIfCancellationRequested();if(IsDisposed)return;status.Text="Cadastro aprovado. Iniciando testes e instalação…";installing=true;
                    Hide();try{using var setup=new SetupWindow(approved,locations,null,registration.InstanceId,reinstallConfirmed?reinstallation:null);setup.ShowDialog();if(setup.InstallSucceeded){try{new GenericRegistrationStore(locations.Data).Complete();}catch(IOException){ /* Optional enrollment cache cleanup cannot invalidate a running service. */ }catch(UnauthorizedAccessException){}}}finally{installing=false;if(!IsDisposed)Show();}Close();return;
                }
                if(reply.Status is "Rejected" or "Expired"){
                    status.ForeColor=Color.FromArgb(153,55,43);status.Text=reply.Status=="Rejected"?"A solicitação foi recusada pela equipe. Confira os dados antes de solicitar novamente.":"A solicitação expirou na central. Solicite um novo código.";
                    registration=null;submit.Text="Solicitar novo código";name.Enabled=true;return;
                }
                status.Text="Aguardando aprovação da equipe. Confirme este código e computador no painel. Fechar preserva o cadastro para retomada.";
                await Task.Delay(TimeSpan.FromSeconds(3),cancellation.Token);reply=await client.Poll(registration,cancellation.Token);
            }
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
        catch(Exception e){Failure(e);}
        finally{polling=false;if(!IsDisposed)submit.Enabled=true;}
    }
    private void Failure(Exception error){status.ForeColor=Color.FromArgb(153,55,43);status.Text=Program.Friendly(error);submit.Text="Tentar novamente";}
    private void WriteQa(DeviceRegistrationStatus reply)
    {
        var root=Path.GetDirectoryName(locations.Data)!;Directory.CreateDirectory(root);var path=Path.Combine(root,"registration.json");var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(new{registrationId=reply.RegistrationId,code=reply.Code,status=reply.Status,expiresAt=reply.ExpiresAt,machineName=reply.MachineName,requestedName=reply.RequestedName}));File.Move(temporary,path,true);
    }
}
