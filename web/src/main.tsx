import {monitorSignal,matchesMonitor,monitorPriority,backupsUpToDate,type MonitorFilter} from './monitoring';
import {deletionPending,deactivationNotice,deletionResultNotice,type DeactivationState} from './updates';
import {AgentUpdates} from './AgentUpdates';
import {DeleteClient} from './DeleteClient';
import { FtpDirectoryTree } from "./FtpDirectoryTree";
import { ServerMonitor } from "./ServerMonitor";
import { TeamAccess } from "./TeamAccess";

import { centralNow, observeCentralDate } from "./centralClock";

const responsivePreview = window.location.port === "5181" && ["localhost", "127.0.0.1"].includes(window.location.hostname);

const localEnvironment = ["localhost", "127.0.0.1", "::1", "[::1]"].includes(window.location.hostname);

import React, { useEffect, useState, type FormEvent } from "react";

import { createRoot } from "react-dom/client";

import "./style.css";

import { ClientOperations, CommunicationAlerts } from "./ClientOperations";

import { ThemeToggle } from "./ThemeToggle";

import { activeClients,attentionReasons,differentialBaseRecovered,executionState,waitingCommands,type DashboardClient } from "./dashboard";

import { RunningBackups } from "./RunningBackups";

import { CompactSchedule } from "./ScheduleSummary";

import { InstallationSettings } from "./InstallationSettings";

import { MachineRegistrations } from "./MachineRegistrations";

type Client = DashboardClient & {

  id: string;

  name: string;

  slug: string;

  database: string;

  machineName: string;

  enabled: boolean;

  expectedIntervalHours: number;

  differentialIntervalHours: number;

  graceMinutes: number;

  lastSeen: string | null;

  lastStatus: string | null;

  lastBackupAt: string | null;

  lastSizeBytes: number | null;

  status: string;

  hasActiveCommand: boolean;

  deletionPending?: boolean;
  deactivationState?: DeactivationState;
  deactivationRequestedAt?: string|null;
  deactivationConfirmedAt?: string|null;
  hasAgentBinding?: boolean;
  agentVersion?:string|null;
  remoteUpdateSupported?:boolean|null;

};

type Run = {

  id: string;

  clientName: string;

  status: string;

  backupType: string;

  finishedAt: string;

  startedAt: string;

  sizeBytes: number;

  fileName: string;

  message: string;

};

type Command = {

  id: string;

  clientId: string;

  clientName: string;

  backupType: string;

  state: string;

  createdAt: string;

};

type Storage = {

  host: string;

  port: number;

  username: string;

  baseDirectory: string;

  hasPassword: boolean;

  password?: string;

};

const initialClient = {

  name: "",

  slug: "",

  database: "",

  expectedIntervalHours: 24,

  differentialIntervalHours: 0,

  graceMinutes: 60,

  enabled: true,

};

const labels: Record<string, string> = {

  Succeeded: "Sucesso reportado",
  Skipped: "Sem alterações",

  Warning: "Com avisos",

  Failed: "Falhou",

  Cancelled: "Cancelado",

  NeverRun: "Sem execução",

  Overdue: "Atrasado",

  Paused: "Pausado",

  Pending: "Aguardando agente",

  Claimed: "Recebido pelo agente",

  Completed: "Finalizado",

  Full: "Completo",

  Differential: "Diferencial",

  Log: "Log",

};

const date = (s: string | null) =>

  s

    ? new Date(s).toLocaleString("pt-BR", {

        day: "2-digit",

        month: "short",

        hour: "2-digit",

        minute: "2-digit",

      })

    : "—";

const bytes = (n: number | null) =>

  n === null

    ? "—"

    : n < 1024

      ? `${n} B`

      : n < 1048576

        ? `${(n / 1024).toFixed(1)} KB`

        : n < 1073741824

          ? `${(n / 1048576).toFixed(1)} MB`

          : `${(n / 1073741824).toFixed(1)} GB`;

function Icon({ name }: { name: string }) {

  const paths: Record<string, React.ReactNode> = {

    overview: (

      <>

        <rect x="3" y="3" width="7" height="7" rx="1" />

        <rect x="14" y="3" width="7" height="7" rx="1" />

        <rect x="3" y="14" width="7" height="7" rx="1" />

        <rect x="14" y="14" width="7" height="7" rx="1" />

      </>

    ),

    clients: (

      <>

        <rect x="3" y="4" width="18" height="12" rx="2" />

        <path d="M8 21h8m-4-5v5" />

      </>

    ),

    history: (

      <>

        <path d="M3 11a9 9 0 1 1 2 7M3 4v7h7m2-4v5l3 2" />

      </>

    ),

    commands: (

      <>

        <path d="m8 4 12 8-12 8z" />

      </>

    ),

    storage: (

      <>

        <ellipse cx="12" cy="5" rx="8" ry="3" />

        <path d="M4 5v7c0 4 16 4 16 0V5M4 12v7c0 4 16 4 16 0v-7" />

      </>

    ),

    refresh: (

      <>

        <path d="M20 7a9 9 0 1 0 1 8M20 2v5h-5" />

      </>

    ),

    check: <path d="m5 12 4 4L20 5" />,

    plus: <path d="M12 5v14M5 12h14" />,

    logout: (

      <>

        <path d="M9 4H4v16h5m5-12 4 4-4 4m-5-4h12" />

      </>

    ),

  };

  return (

    <svg

      width="20"

      height="20"

      viewBox="0 0 24 24"

      fill="none"

      stroke="currentColor"

      strokeWidth="1.7"

      strokeLinecap="round"

      strokeLinejoin="round"

      aria-hidden="true"

    >

      {paths[name] || paths.storage}

    </svg>

  );

}

function Badge({ status }: { status: string }) {

  return (

    <span className={"badge " + status.toLowerCase()}>

      <i />

      {labels[status] || status}

    </span>

  );

}

async function api(path: string, method = "GET", body?: unknown) {

  const startedAt = Date.now();

  const response = await fetch("/api" + path, {

    method,

    headers: {

      "Content-Type": "application/json",

      "X-Requested-With": "BackupControl",

    },

    body: body === undefined ? undefined : JSON.stringify(body),

  });

  observeCentralDate(response.headers.get("Date"), startedAt, Date.now());

  if (response.status === 401) {

    if (path !== "/auth/login")

      window.dispatchEvent(new Event("session-expired"));

    throw new Error("Sessão expirada ou credenciais inválidas.");

  }

  const text = await response.text();

  let data;

  try {

    data = JSON.parse(text);

  } catch {

    data = { error: text };

  }

  if (!response.ok)

    throw new Error(data.error || "Não foi possível concluir a operação.");

  return data;

}

function App() {



  const [attentionOpen,setAttentionOpen]=useState(false);
  const [upToDateOpen,setUpToDateOpen]=useState(false);
  const [attentionClientId,setAttentionClientId]=useState<string|null>(null);
  function openAttention(clientId:string|null=null){setAttentionClientId(clientId);setAttentionOpen(true);}

  const [runningOpen,setRunningOpen]=useState(false);

  const [monitorFilter,setMonitorFilter]=useState<MonitorFilter>('all');
  const [clientStatusFilter,setClientStatusFilter]=useState('all');

  const [operationsClientId,setOperationsClientId]=useState<string|null>(null);

  const [savedBaseDirectory,setSavedBaseDirectory]=useState<string|null>(null);
  const [updatesOpen,setUpdatesOpen]=useState(false);
  const [deleteClient,setDeleteClient]=useState<Client|null>(null);
  const [userRole,setUserRole]=useState('');
  const [settingsOpen,setSettingsOpen]=useState(false);
  const [serverMonitorOpen,setServerMonitorOpen]=useState(false);

  const [settingsTab,setSettingsTab]=useState("sql");

  const [apiConnected,setApiConnected]=useState<boolean|null>(null);

  const [refreshError,setRefreshError]=useState("");

  const [backupTypes,setBackupTypes]=useState<Record<string,string>>({});

  const [user, setUser] = useState<string | null>(null),

    [loading, setLoading] = useState(true),

    [page, setPage] = useState("overview");

  const [clients, setClients] = useState<Client[]>([]),

    [runs, setRuns] = useState<Run[]>([]),

    [commands, setCommands] = useState<Command[]>([]),

    [storage, setStorage] = useState<Storage>({

      host: "",

      port: 21,

      username: "",

      baseDirectory: "/backup",

      hasPassword: false,

    });

  const [error, setError] = useState(""),

    [notice, setNotice] = useState(""),

    [busy, setBusy] = useState(false),

    [query, setQuery] = useState("");

  const [modal, setModal] = useState(false),

    [edit, setEdit] = useState<Client | null>(null),

    [form, setForm] = useState(initialClient);

  const [login, setLogin] = useState({ username: "admin", password: "" }),

    [detail, setDetail] = useState<Run | null>(null),

    [lastUpdate, setLastUpdate] = useState<Date | null>(null);

  async function refresh(includeStorage = false) {

    try {

    const [c, r, q, s] = await Promise.all([

      api("/clients"),

      api("/runs"),

      api("/commands"),

      api("/storage"),

    ]);

    setClients(c);
    setSavedBaseDirectory(s.baseDirectory);

    setRuns(r);

    setCommands(q);

    if (includeStorage) setStorage(s);

    setLastUpdate(new Date(centralNow()));

    setApiConnected(true);

    setRefreshError("");

    }catch(e){setApiConnected(false);setRefreshError(e instanceof Error?e.message:'Falha ao atualizar os dados da central.');throw e;}

  }

  async function action(fn: () => Promise<void>) {

    setBusy(true);

    setError("");

    setNotice("");

    try {

      await fn();

    } catch (e) {

      setError((e as Error).message);

    } finally {

      setBusy(false);

    }

  }

  useEffect(() => {

    const expired = () => {

      setUser(null);

      setApiConnected(null);

      setRefreshError("");

      setStorage({

        host: "",

        port: 21,

        username: "",

        baseDirectory: "/backup",

        hasPassword: false,

      });



      setLogin({ username: "admin", password: "" });

    };

    window.addEventListener("session-expired", expired);

    return () => window.removeEventListener("session-expired", expired);

  }, []);

  useEffect(() => {

    api("/auth/me")

      .then((x) => {setUser(x.username);setUserRole(x.role||'');})

      .catch(() => {})

      .finally(() => setLoading(false));

  }, []);

  useEffect(() => {

    if (!user) return;

    refresh(true).catch(() => {});

    const id = setInterval(

      () => refresh().catch(() => {}),

      15000,

    );

    return () => clearInterval(id);

  }, [user]);

  const current = clients.filter((c) =>

    (c.name + " " + c.slug + " " + c.database)

      .toLowerCase()

      .includes(query.toLowerCase()) && (page!=='overview'||matchesMonitor(c,monitorFilter)) && (page==='clients'||c.enabled) && (page!=='clients'||clientStatusFilter==='all'||deletionPending(c)||c.enabled===(clientStatusFilter==='active')),

  );

  if(page==='overview')current.sort((a,b)=>monitorPriority(a)-monitorPriority(b)||a.name.localeCompare(b.name,'pt-BR'));

  const upToDateClients=activeClients(clients).filter(c=>backupsUpToDate(c));
  const good = upToDateClients.length,

    problems = clients.filter(c=>attentionReasons(c).length>0).length;
  const attentionClients=clients.filter(c=>(attentionClientId===null||c.id===attentionClientId)&&attentionReasons(c).length>0);

  function openClient(c?: Client) {

    setEdit(c || null);

    setForm(

      c

        ? {

            name: c.name,

            slug: c.slug,

            database: c.database,

            expectedIntervalHours: c.expectedIntervalHours,

            differentialIntervalHours: c.differentialIntervalHours||0,

            graceMinutes: c.graceMinutes,

            enabled: c.enabled,

          }

        : initialClient,

    );

    setModal(true);

    setError("");

  }

  async function saveClient(e: FormEvent) {

    e.preventDefault();

    await action(async () => {

      await api(

        "/clients" + (edit ? "/" + edit.id : ""),

        edit ? "PUT" : "POST",

        form,

      );

      setModal(false);

      setPage("clients");

      await refresh();

      setNotice("Cliente salvo. Execute o instalador genérico na máquina e aprove a solicitação em Clientes.");

    });

  }

  async function queue(c: Client) {

    await action(async () => {

      await api(`/clients/${c.id}/commands`, "POST", { backupType: backupTypes[c.id]||"Full" });

      await refresh();

      setNotice(

        "Solicitação criada. Aguardando um agente Windows autenticado buscar o comando.",

      );

    });

  }

  const clientCollection = (
    <ul className={'client-collection '+(page==='clients'?'client-management-collection':'client-overview-collection')} aria-label={page==='clients'?'Clientes cadastrados':'Backups por cliente'}>
      {current.map(c=>{
        const reasons=attentionReasons(c),state=executionState(c),signal=monitorSignal(c);
        const connected=!!c.lastSeen&&centralNow()-new Date(c.lastSeen).getTime()<120000;
        return <li key={c.id}><article className={'client-card signal-'+signal.tone+(reasons.length?' has-attention':'')+(state==='running'?' is-running':'')} aria-label={'Cliente '+c.name}>
          <div className="client-card-heading">
            <div className="client-identity"><button className="client-history-icon" aria-label={`Abrir histórico e arquivos de ${c.name}`} title="Histórico, arquivos e comunicação" onClick={()=>setOperationsClientId(c.id)}><Icon name="history"/></button><button className="client-name-button" onClick={()=>setOperationsClientId(c.id)}>{c.name}</button></div>
            <span className={'agent-presence '+(connected?'online':'offline')}><i aria-hidden="true"/>{connected?'Conectado':c.lastSeen?'Sem contato':'Não conectado'}</span>
          </div>
          <div className="client-card-machine"><span>{c.machineName||'Aguardando instalação'}</span><span>{c.database}</span></div>
          {page==='clients'?<div className="client-card-registration"><span className={`badge ${c.enabled?'succeeded':'paused'}`}>{deletionPending(c)?c.deactivationState==='Confirmed'?'Desativação confirmada':c.deactivationState==='RequiresUpgrade'?'Reinstalação necessária':'Aguardando agente':c.enabled?'Ativo':'Desativado'}</span><span className="client-version">Agente {c.agentVersion||'não informado'}</span>{deletionPending(c)&&<small>{deactivationNotice(c.deactivationState)}</small>}{c.deactivationRequestedAt&&<small>Solicitada em {date(c.deactivationRequestedAt)}</small>}{c.deactivationConfirmedAt&&<small>Confirmada em {date(c.deactivationConfirmedAt)}</small>}</div>:<div className="client-card-result">
            <div className="monitor-signal"><strong>{signal.label}</strong>{state==='running'&&<span>{c.agentTelemetry?.currentOrigin==='Scheduled'?'AGENDADO':c.agentTelemetry?.currentOrigin==='Manual'?'MANUAL':'EM ANDAMENTO'}</span>}</div>
            {state==='running'?<button className="signal-detail text-button" onClick={()=>setRunningOpen(true)}>{signal.detail} · Ver etapa</button>:reasons.length?<button className="signal-detail text-button" onClick={()=>openAttention(c.id)}>{signal.detail} · Ver motivo</button>:<span className="signal-detail">{signal.detail}</span>}
            <span className="client-result-date">Último backup: {c.lastBackupAt?date(c.lastBackupAt):'ainda não enviado'}{c.lastSizeBytes?` · ${bytes(c.lastSizeBytes)}`:''}</span>
          </div>}

          {page!=='clients'&&<div className="client-card-schedule"><span className="client-field-label">Próximos backups</span><CompactSchedule client={c}/></div>}
          <div className="client-card-health">{reasons.length>0?<button className="client-alert-button" onClick={()=>openAttention(c.id)}><span>{reasons.length} {reasons.length===1?'alerta':'alertas'} para verificar</span><span aria-hidden="true">Ver motivos →</span></button>:<span className="client-health-ok">{c.enabled?differentialBaseRecovered(c)?'Base recuperada · aguardando próximo diferencial':'Nenhum alerta no momento':'Rotinas pausadas'}</span>}</div>
          {page==='clients'&&<small className="client-last-contact">Último contato: {date(c.lastSeen)}</small>}
          <div className="client-card-actions">
            <button className="secondary client-files-button" onClick={()=>setOperationsClientId(c.id)}>Backups e arquivos</button>
            {page==='clients'?<><button className="text-button" onClick={()=>openClient(c)}>Gerenciar</button><details className="client-card-menu"><summary aria-label={'Opções de '+c.name}>Opções</summary><div className="client-menu-items"><button className={c.enabled?'danger-text':'text-button'} disabled={busy||deletionPending(c)} onClick={()=>action(async()=>{
              await api(`/clients/${c.id}/status`,'PATCH',{enabled:!c.enabled});await refresh();
              setNotice(c.enabled?'Cliente desativado. Histórico e backups preservados; solicitações pendentes canceladas. O agente aplica o bloqueio no próximo contato.':'Cliente reativado. O agente recebe a configuração no próximo contato.');
            })}>{c.enabled?'Desativar':'Ativar'}</button>{userRole==='Admin'&&!c.enabled&&<button className="danger-text" disabled={busy} onClick={()=>action(async()=>{const configured=await api('/storage');setSavedBaseDirectory(configured.baseDirectory);setDeleteClient(c);})}>Excluir definitivamente</button>}</div></details></>:<div className="client-run-actions"><select className="backup-type-select" aria-label={`Tipo de backup de ${c.name}`} value={backupTypes[c.id]||'Full'} onChange={e=>setBackupTypes({...backupTypes,[c.id]:e.target.value})} disabled={busy||c.hasActiveCommand}><option value="Full">Completo</option><option value="Differential">Diferencial</option></select><button className="small-button" disabled={busy||c.hasActiveCommand||!c.enabled} onClick={()=>queue(c)}><Icon name="commands"/>{c.hasActiveCommand?'Na fila':'Executar'}</button></div>}
          </div>
        </article></li>;
      })}
    </ul>
  );

  if (loading)

    return <div className="loading">Conectando ao Backup Control…</div>;

  if (!user)

    return (

      <div className="login-page">

        <div className="login-theme"><ThemeToggle /></div>

        <div className="login-story">

          <div className="brand">

            <span className="brand-icon">

              <Icon name="storage" />

            </span>

            backup<span>control</span>

          </div>

          <div>

            <span className="eyebrow">VISIBILIDADE PARA SUA OPERAÇÃO</span>

            <h1>

              Seus backups.

              <br />

              Sob controle.

            </h1>

            <p>

              Um lugar para acompanhar cada cliente,

              <br />

              cada rotina e cada resultado.

            </p>

            <div className="login-lines">

              <i />

              <i />

              <i />

            </div>

          </div>

          <small>Acesso exclusivo da equipe</small>

        </div>

        <form

          className="login-form"

          onSubmit={(e) => {

            e.preventDefault();

            action(async () => {

              const r = await api("/auth/login", "POST", login);

              setUser(r.username);setUserRole(r.role||'');

            });

          }}

        >

          <span className="eyebrow">BEM-VINDO DE VOLTA</span>

          <h2>Acesse sua central</h2>

          <p>Use suas credenciais da equipe para continuar.</p>{notice&&<div className="alert success" role="status">{notice}</div>}

          <label>

            Usuário

            <input

              autoComplete="username"

              required

              value={login.username}

              onChange={(e) => setLogin({ ...login, username: e.target.value })}

            />

          </label>

          <label>

            Senha

            <input

              autoComplete="current-password"

              type="password"

              required

              value={login.password}

              onChange={(e) => setLogin({ ...login, password: e.target.value })}

            />

          </label>

          {error && (

            <div className="alert error" role="alert">

              {error}

            </div>

          )}

          <button className="primary" disabled={busy}>

            {busy ? "Entrando…" : "Entrar no painel →"}

          </button>

          <small>{localEnvironment ? "Ambiente local" : "Servidor de produção"} · acesso exclusivo da equipe</small>

        </form>

      </div>

    );

  const pages: Record<string, { title: string; subtitle: string }> = {

    overview: {

      title: "Visão geral",

      subtitle: "O estado dos backups da sua operação, em um só lugar.",

    },

    clients: {

      title: "Gerenciamento de clientes",

      subtitle:

        "Cadastre, configure, desative ou reative máquinas sem apagar seus backups.",

    },

    history: {

      title: "Histórico de backups",

      subtitle:

        "Resultados reportados pelos agentes, sem confundir envio com restauração.",

    },

    files: {title:"Catálogo de backups",subtitle:"Arquivos reais no FTP, organizados por cliente e disponíveis para download."},

    commands: {

      title: "Fila de execução",

      subtitle: "Acompanhe as solicitações enviadas aos agentes Windows.",

    },

    installation: { title: "Instalação", subtitle: "Endereço da central e padrões SQL compartilhados para novas instalações e reparos." },

    storage: {

      title: "Destino compartilhado",

      subtitle: "Uma configuração de FTP para todas as máquinas cadastradas.",

    },

  };

  return (

    <div className="app monitoring-workspace">

      <aside>

        <div className="brand">

          <span className="brand-icon">

            <Icon name="storage" />

          </span>

          backup<span>control</span>

        </div>

        <div className="workspace">

          <span className="workspace-avatar">EQ</span>

          <div>

            <strong>Equipe de operações</strong>

            <small>Central de backups</small>

          </div>

        </div>

        <span className="nav-caption">GERENCIAMENTO</span>

        <nav>

          {[

            ["overview", "Visão geral"],

            ["clients", "Clientes"],











          ].map(([key, label]) => (

            <button

              key={key}

              className={page === key ? "active" : ""}
              aria-current={page===key?"page":undefined}

              onClick={() => {

                setPage(key);

                setQuery("");

                setError("");

                setNotice("");

              }}

            >

              <Icon name={key} />

              {label}

              {key === "commands" &&

                commands.filter((c) => c.state === "Pending").length > 0 && (

                  <b>{commands.filter((c) => c.state === "Pending").length}</b>

                )}

            </button>

          ))}

        </nav>

        <div className="sidebar-bottom">

          <div className="environment">

            <i />

            {localEnvironment ? "Ambiente local" : "Servidor de produção"}<span>{localEnvironment ? "Docker Compose" : window.location.hostname}</span>

          </div>

          <button

            onClick={() =>

              action(async () => {

                await api("/auth/logout", "POST", {});

                setUser(null);

                setLogin({ ...login, password: "" });

              })

            }

          >

            <Icon name="logout" />

            Sair <span>{user}</span>

          </button>

        </div>

      </aside>

      <main>

        <header>

          <div className="mobile-brand">backup<span>control</span></div><div className="breadcrumb">

            Central de backups <span>/</span> {pages[page].title}

          </div>

          <div className="header-right"><button className="secondary" aria-label="Configurações" onClick={()=>setSettingsOpen(true)}><span>Configurações</span></button>

            <ThemeToggle />

            <span className={'api-connection '+(apiConnected===false?'api-disconnected':'')} role="status"><span className="live-dot" />{apiConnected===true?'API conectada':apiConnected===false?'API sem atualização':'Conectando à API'}{apiConnected===false&&<small>{lastUpdate?'Última atualização: '+lastUpdate.toLocaleTimeString('pt-BR'):'Nenhuma atualização concluída'}</small>}</span>{" "}

            <span className="avatar">{user.slice(0, 2).toUpperCase()}</span>

          </div>

        </header>

        <div className="content">
          {responsivePreview&&<div className="preview-environment" role="note"><strong>Prévia local</strong><span>Dados fictícios · Ambiente isolado de testes</span></div>}

          {refreshError&&<div className="alert error" role="alert">API sem atualização. Os dados exibidos são da última consulta concluída. {refreshError}</div>}

          <div className="page-title">

            <div>

              <span className="eyebrow">CONTROLE DA OPERAÇÃO</span>

              <h1>{page==='overview'?'Monitoramento da operação':pages[page].title}</h1>

              <p>{pages[page].subtitle}</p>

            </div>

            <div className="title-actions">
              <button className="secondary" onClick={()=>setServerMonitorOpen(true)}>Monitor do servidor</button>

              <a className="secondary universal-installer" href="/api/installer/download" download>Baixar instalador genérico</a>

              <button

                className="secondary"

                disabled={busy}

                onClick={() => action(refresh)}

                title="Atualizar dados"

              >

                <Icon name="refresh" />

                Atualizar

              </button>

              {page !== "storage" && page !== "installation" && (

                <button className="primary" onClick={() => openClient()}>

                  <Icon name="plus" />

                  Novo cliente

                </button>

              )}

            </div>

          </div>

          {error && (

            <div className="alert error" role="alert">

              {error}

              <button onClick={() => setError("")}>×</button>

            </div>

          )}

          {notice && (

            <div className="alert success" role="status">

              {notice}

              <button onClick={() => setNotice("")}>×</button>

            </div>

          )}

          {page === "overview" && (

            <>

              <div className="stats">

                {[

                  [

                    "Clientes ativos",

                    activeClients(clients).length,

                    "Uma máquina por cliente",

                    "clients",

                  ],

                  [

                    "Backups em dia",

                    good,

                    "Dentro do prazo e sem pendências",

                    "check",

                  ],

                  [

                    "Precisam de atenção",

                    problems,

                    "Clique para ver clientes e motivos",

                    "history",

                  ],

                  [

                    "Atividade dos backups",

                    `${clients.filter(c=>executionState(c)==='running').length} executando`,

                    `Manuais e agendados · ${waitingCommands(commands,clients).length} aguardando · ${clients.filter(c=>executionState(c)==='stale').length} sem atualização`,

                    "commands",

                  ],

                ].map(([title, value, note, icon], i) => (

                  <div className={"stat stat-" + i+((i===1||i===2||i===3)?' stat-clickable':'')} key={title} role={(i===1||i===2||i===3)?'button':undefined} tabIndex={(i===1||i===2||i===3)?0:undefined} aria-label={i===1?`Backups em dia: ${value} cliente(s)`:i===2?`Precisam de atenção: ${value} cliente(s)`:i===3?`Atividade dos backups: ${value}, ${note}`:undefined} onClick={i===1?()=>setUpToDateOpen(true):i===2?()=>openAttention():i===3?()=>setRunningOpen(true):undefined} onKeyDown={(i===1||i===2||i===3)?e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();if(i===1)setUpToDateOpen(true);else if(i===2)openAttention();else setRunningOpen(true);}}:undefined}>

                    <div>

                      <span>{title}</span>

                      <span className="stat-icon">

                        <Icon name={String(icon)} />

                      </span>

                    </div>

                    <strong>{value}</strong>

                    <small>{note}</small>

                  </div>

                ))}

              </div>

              <div className="integration-note">

                <span className="note-icon">i</span>

                <div>

                  <strong>Conecte as máquinas dos seus clientes</strong>

                  <p>

                    Configure os padrões em Configurações e execute o mesmo instalador genérico nas máquinas. Aprove cada solicitação em Clientes.

                    Ele executa os backups localmente e envia os arquivos diretamente ao FTP.

                  </p>

                </div>

                <button

                  className="text-button"

                  onClick={() => setPage("clients")}

                >

                  Cadastrar cliente →

                </button>

              </div>

            </>

          )}

          {page === "clients" && <MachineRegistrations api={api} clients={clients} onChanged={refresh}/>}

          {(page === "clients" || page === "overview") && (

            <section className="panel client-panel">

              <div className="panel-heading">

                <div>

                  <h2>

                    {page === "overview"

                      ? "Seus clientes"

                      : "Clientes cadastrados"}{" "}

                    <span className="count">{page==='clients'?clients.length:activeClients(clients).length}</span>

                  </h2>

                  <p>

                    O sucesso é informado pelo agente; a restauração exige

                    validação separada.

                  </p>

                </div>

                <div className="client-section-tools"><input

                  className="search"

                  aria-label="Buscar cliente"

                  placeholder="Buscar cliente ou banco…"

                  value={query}

                  onChange={(e) => setQuery(e.target.value)}

                /><button className="secondary" onClick={()=>setUpdatesOpen(true)}>Atualizações dos agentes</button></div>

              </div>


              {page==='overview'&&<div className="monitor-toolbar"><div className="monitor-filters" role="group" aria-label="Filtrar monitoramento">{([['all','Todos'],['problems','Problemas'],['running','Executando'],['healthy','Em dia']] as const).map(([value,label])=><button key={value} aria-pressed={monitorFilter===value} onClick={()=>setMonitorFilter(value)}>{label}<span>{activeClients(clients).filter(c=>matchesMonitor(c,value)).length}</span></button>)}</div><div className="monitor-legend" aria-label="Legenda dos estados"><span className="monitor-order-note">Prioridade operacional</span><span className="legend-running">Executando</span><span className="legend-failed">Falha</span><span className="legend-attention">Atenção</span><span className="legend-healthy">Sucesso</span></div></div>}
              {page==='clients'&&<div className="client-management-toolbar"><label>Exibir clientes<select value={clientStatusFilter} onChange={e=>setClientStatusFilter(e.target.value)}><option value="all">Todos</option><option value="active">Ativos</option><option value="inactive">Desativados</option></select></label><p>{clients.filter(c=>c.enabled).length} ativos · {clients.filter(c=>!c.enabled).length} desativados. Desativar preserva histórico e arquivos. Operações já iniciadas não são canceladas; agentes sem conexão só recebem o bloqueio ao reconectar.</p></div>}

              {clients.length === 0 ? (

                <div className="empty">

                  <span className="empty-icon">

                    <Icon name="clients" />

                  </span>

                  <h3>Sua operação começa pelo primeiro cliente</h3>

                  <p>

                    Cadastre uma máquina para definir a pasta de destino

                    <br />e obter a credencial de integração do agente.

                  </p>

                  <button className="primary" onClick={() => openClient()}>

                    <Icon name="plus" />

                    Cadastrar primeiro cliente

                  </button>

                </div>

              ) : current.length ? (

                clientCollection

              ) : (

                <div className="empty">{page==='overview'&&monitorFilter!=='all'?'Nenhum cliente neste filtro.':page==='overview'&&activeClients(clients).length===0?'Nenhum cliente ativo. Ative ou cadastre um cliente em Clientes.':'Nenhum cliente encontrado.'}</div>

              )}

            </section>

          )}

          {page === "overview" && (

            <div className="bottom-grid">

              <section className="panel compact">

                <h2>Últimos resultados</h2>

                {runs.length ? (

                  runs.slice(0, 4).map((r) => (

                    <div className="activity" key={r.id}>

                      <span

                        className={"activity-dot " + r.status.toLowerCase()}

                      />

                      <div>

                        <strong>{r.clientName}</strong>

                        <small>

                          {labels[r.backupType]} · {date(r.finishedAt)}

                        </small>

                      </div>

                      <Badge status={r.status} />

                    </div>

                  ))

                ) : (

                  <p className="muted">

                    Nenhuma execução reportada. Os resultados aparecerão aqui

                    após a conexão dos agentes.

                  </p>

                )}

              </section>

              <section className="panel compact storage-card">

                <span className="eyebrow">ARMAZENAMENTO CENTRAL</span>

                <h2>

                  {storage.host

                    ? "FTP configurado"

                    : "Conecte seu armazenamento"}

                </h2>

                <p>

                  {storage.host ||

                    "Defina o servidor, o usuário e a senha compartilhados."}

                </p>

                <code>

                  {storage.baseDirectory}/{"{cliente}"}/

                </code>

                <button

                  className="text-button"

                  onClick={() => {setSettingsTab("ftp");setSettingsOpen(true);}}

                >

                  Configurar destino →

                </button>

              </section>

            </div>

          )}

          {settingsOpen&&<div className="modal-backdrop" onClick={()=>setSettingsOpen(false)}><section className="modal configuration-modal" role="dialog" aria-modal="true" aria-labelledby="configuration-title" onClick={e=>e.stopPropagation()} onKeyDown={e=>{if(e.key==='Escape')setSettingsOpen(false);if(e.key==='Tab'){const controls=e.currentTarget.querySelectorAll<HTMLElement>('button:not([disabled]),a[href],input:not([disabled]),select:not([disabled])');const first=controls[0],last=controls[controls.length-1];if(e.shiftKey&&document.activeElement===first){e.preventDefault();last?.focus();}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus();}}}}><div className="modal-title"><h2 id="configuration-title">Configurações compartilhadas</h2><button autoFocus className="close" aria-label="Fechar configurações" onClick={()=>setSettingsOpen(false)}>×</button></div><div className="operation-tabs" role="tablist" aria-label="Configurações"><button role="tab" aria-selected={settingsTab==='sql'} className={settingsTab==='sql'?'active':''} onClick={()=>setSettingsTab('sql')}>Central e SQL</button><button role="tab" aria-selected={settingsTab==='ftp'} className={settingsTab==='ftp'?'active':''} onClick={()=>setSettingsTab('ftp')}>Armazenamento FTP</button><button role="tab" aria-selected={settingsTab==='users'} className={settingsTab==='users'?'active':''} onClick={()=>setSettingsTab('users')}>Usuários e senha</button></div>{settingsTab==='users'&&<TeamAccess api={api} onPasswordChanged={()=>{setSettingsOpen(false);setUser(null);setLogin({username:user||"",password:""});setNotice("Senha alterada. Entre novamente com sua nova senha.");}}/>}{settingsTab==='ftp'&&error&&<div className="alert error" role="alert">{error}</div>}{settingsTab==='ftp'&&notice&&<div className="alert success" role="status">{notice}</div>}{settingsTab==='sql'&&<InstallationSettings api={api}/>}          {settingsTab === "ftp" && (

            <div className="settings-grid">

              <form

                className="panel settings-form"

                onSubmit={(e) => {

                  e.preventDefault();

                  action(async () => {

                    await api("/storage", "PUT", storage);

                    await refresh(true);

                    setNotice(

                      "Destino FTP salvo. A senha não é devolvida ao navegador.",

                    );

                  });

                }}

              >

                <div className="panel-heading">

                  <div>

                    <h2>Conexão FTP</h2>

                    <p>

                      A mesma credencial será utilizada por todos os clientes.

                    </p>

                  </div>

                  <span className="badge neutral">Compartilhado</span>

                </div>

                <label>

                  Servidor

                  <input

                    required

                    placeholder="IPv4, IPv6 ou domínio — sem ftp://"

                    value={storage.host}

                    onChange={(e) =>

                      setStorage({ ...storage, host: e.target.value })

                    }

                  />

                </label>

                <div className="form-grid">

                  <label>

                    Porta

                    <input

                      required

                      type="number"

                      min="1"

                      max="65535"

                      value={storage.port}

                      onChange={(e) =>

                        setStorage({ ...storage, port: Number(e.target.value) })

                      }

                    />

                  </label>

                  <label>

                    Modo de transferência

                    <input value="Passivo estendido (EPSV)" disabled />

                  </label>

                </div>

                <label>

                  Usuário

                  <input

                    required

                    autoComplete="off"

                    value={storage.username}

                    onChange={(e) =>

                      setStorage({ ...storage, username: e.target.value })

                    }

                  />

                </label>

                <label>

                  Senha

                  <input

                    type="password"

                    autoComplete="new-password"

                    placeholder={

                      storage.hasPassword

                        ? "Senha já salva. Deixe vazio para manter."

                        : "Informe a senha compartilhada"

                    }

                    value={storage.password || ""}

                    onChange={(e) =>

                      setStorage({ ...storage, password: e.target.value })

                    }

                  />

                </label>

                <label>

                  Diretório base

                  <input

                    required

                    value={storage.baseDirectory}

                    onChange={(e) =>

                      setStorage({ ...storage, baseDirectory: e.target.value })

                    }

                  />

                </label>

                <div className="form-footer">

                  <button

                    type="button"

                    className="secondary"

                    disabled={busy || !storage.hasPassword}

                    onClick={() =>

                      action(async () => {

                        const result = await api("/storage/test", "POST", {});

                        setNotice(result.message);

                      })

                    }

                  >

                    Testar configuração salva

                  </button>

                  <button className="primary" disabled={busy}>

                    {busy ? "Aguarde…" : "Salvar destino"}

                  </button>

                </div>

              </form>

              <div>

                <FtpDirectoryTree api={api}/>

                <div className="plain-note">

                  <strong>Conexão atual: FTP sem TLS</strong>

                  <p>

                    As credenciais e os arquivos trafegam sem criptografia. A

                    senha compartilhada permite acesso a todas as pastas. O

                    acesso externo depende da conectividade do servidor.

                  </p>

                </div>

              </div>

            </div>

          )}

</section></div>}

          <footer>

            <span>

              Backup Control <b>·</b> Central da equipe

            </span>

            <span>

              {lastUpdate

                ? "Atualizado às " + lastUpdate.toLocaleTimeString("pt-BR")

                : "Carregando…"}{" "}

              · horários locais

            </span>

          </footer>

        </div>

      </main>

      {serverMonitorOpen&&<ServerMonitor api={api} onClose={()=>setServerMonitorOpen(false)}/>}
      {updatesOpen&&<AgentUpdates clients={clients} api={api} isAdmin={userRole==='Admin'} onClose={()=>setUpdatesOpen(false)}/>}
      {deleteClient&&!deleteClient.enabled&&savedBaseDirectory!==null&&userRole==='Admin'&&<DeleteClient client={deleteClient} baseDirectory={savedBaseDirectory} api={api} onClose={()=>setDeleteClient(null)} onDeleted={async result=>{await refresh();setNotice(deletionResultNotice(result));}}/>}
      {operationsClientId&&clients.some(c=>c.id===operationsClientId)&&<ClientOperations key={operationsClientId} client={clients.find(c=>c.id===operationsClientId)!} api={api} onClose={()=>setOperationsClientId(null)}/>}

      {runningOpen && <RunningBackups clients={clients} commands={commands} onClose={()=>setRunningOpen(false)}/>}

      {upToDateOpen && <div className="modal-backdrop" onClick={()=>setUpToDateOpen(false)}><section className="modal attention-modal" role="dialog" aria-modal="true" aria-labelledby="up-to-date-title" onClick={e=>e.stopPropagation()} onKeyDown={e=>{if(e.key==='Escape')setUpToDateOpen(false);if(e.key==='Tab'){const items=e.currentTarget.querySelectorAll<HTMLButtonElement>('button:not([disabled])');const first=items[0],last=items[items.length-1];if(e.shiftKey&&document.activeElement===first){e.preventDefault();last?.focus();}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus();}}}}>
        <div className="modal-title"><h2 id="up-to-date-title">Backups em dia ({upToDateClients.length})</h2><button autoFocus className="close" aria-label="Fechar backups em dia" onClick={()=>setUpToDateOpen(false)}>×</button></div>
        <p>Clientes ativos com completo confirmado, rotinas dentro do intervalo e da tolerância, agente em contato e sem alertas pendentes. Avaliações sem alterações aceitas pela central mantêm o prazo; não representam um novo arquivo.</p>
        <p>Este indicador acompanha a rotina e o envio reportado. Não confirma uma restauração testada.</p>
        {upToDateClients.length===0?<p>Nenhum cliente atende aos critérios neste momento.</p>:upToDateClients.map(c=><article className="attention-client" key={c.id}><div className="attention-client-heading"><div><h3>{c.name}</h3><small>{c.machineName||'Máquina não informada'} · {c.database}</small></div><button className="secondary" onClick={()=>{setUpToDateOpen(false);setOperationsClientId(c.id);}}>Histórico e arquivos</button></div><p>Último completo: {new Date(c.lastFullBackupAt!).toLocaleString('pt-BR')}{c.differentialIntervalHours>0&&<> · Último diferencial: {c.lastDifferentialBackupAt?new Date(c.lastDifferentialBackupAt).toLocaleString('pt-BR'):'Ainda não necessário após o completo'}</>}</p></article>)}
      </section></div>}
      {attentionOpen && <div className="modal-backdrop" onClick={()=>setAttentionOpen(false)}><section className="modal attention-modal" role="dialog" aria-modal="true" aria-labelledby="attention-title" onClick={e=>e.stopPropagation()} onKeyDown={e=>{if(e.key==='Escape')setAttentionOpen(false);if(e.key==='Tab'){const controls=e.currentTarget.querySelectorAll<HTMLButtonElement>('button:not([disabled])');const first=controls[0],last=controls[controls.length-1];if(e.shiftKey&&document.activeElement===first){e.preventDefault();last?.focus();}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus();}}}}>

        <div className="modal-title"><div><span className="eyebrow">ACOMPANHAMENTO DA OPERAÇÃO</span><h2 id="attention-title">{attentionClientId===null?'Clientes que precisam de atenção':'Alertas desta máquina'}</h2></div><button autoFocus className="close" aria-label="Fechar atenção" onClick={()=>setAttentionOpen(false)}>×</button></div>

        <p>{attentionClientId===null?`${attentionClients.length} cliente(s) com pendências. Cada cliente é contado uma vez. Clientes pausados não entram nesta lista.`:'Somente os alertas da máquina selecionada. As informações são atualizadas automaticamente.'}</p>

        {attentionClients.length===0?<div className="empty"><h3>{attentionClientId===null?'Nenhum cliente precisa de atenção':'Esta máquina não possui alertas ativos'}</h3><p>As agendas e os últimos resultados disponíveis estão em dia.</p></div>:attentionClients.map(c=>{const reasons=attentionReasons(c);return reasons.length>0&&<article className="attention-client" key={c.id}><div className="attention-client-heading"><div><h3>{c.name}</h3><small>{c.machineName||'Agente não identificado'} · {c.database}</small></div><button className="secondary" onClick={()=>{setAttentionOpen(false);openClient(c);}}>Gerenciar cliente</button></div><ul>{reasons.map(reason=><li key={reason.code}><strong>{reason.message}</strong>{reason.detail&&<pre className="wrap attention-error">{reason.detail}</pre>}{reason.since&&<small>Referência: {new Date(reason.since).toLocaleString('pt-BR')}</small>}</li>)}</ul><button className="text-button" onClick={()=>{setAttentionOpen(false);setOperationsClientId(c.id);}}>Consultar histórico de execuções</button></article>;})}

      </section></div>}

      {modal && (

        <div className="modal-backdrop">

          <form className="modal" onSubmit={saveClient}>

            <div className="modal-title">

              <div>

                <span className="eyebrow">CLIENTE / MÁQUINA</span>

                <h2>{edit ? "Gerenciar cliente" : "Novo cliente"}</h2>

              </div>

              <button

                type="button"

                className="close"

                onClick={() => setModal(false)}

                aria-label="Fechar"

              >

                ×

              </button>

            </div>

            <label>

              Nome do cliente

              <input

                autoFocus

                required

                maxLength={120}

                value={form.name}

                onChange={(e) => setForm({ ...form, name: e.target.value })}

              />

            </label>

            <label>

              Identificador da pasta

              <input

                required

                disabled={!!edit}

                pattern="[a-z0-9][a-z0-9_-]{1,62}"

                placeholder="cliente01"

                value={form.slug}

                onChange={(e) =>

                  setForm({ ...form, slug: e.target.value.toLowerCase() })

                }

              />

              <small>

                Destino: {storage.baseDirectory}/{form.slug || "{cliente}"}/ ·

                permanente

              </small>

            </label>

            <label>

              Banco SQL Server

              <input

                required

                maxLength={128}

                placeholder="ATXDADOS"

                value={form.database}

                onChange={(e) => setForm({ ...form, database: e.target.value })}

              />

            </label>

            <div className="form-grid">

              <label>

                Completo a cada (horas)

                <input

                  type="number"

                  min={1}

                  max={8760}

                  required

                  value={form.expectedIntervalHours}

                  onChange={(e) =>

                    setForm({

                      ...form,

                      expectedIntervalHours: Number(e.target.value),

                    })

                  }

                />

              </label>

              <label>

                Tolerância (minutos)

                <input

                  type="number"

                  min={0}

                  max={10080}

                  required

                  value={form.graceMinutes}

                  onChange={(e) =>

                    setForm({ ...form, graceMinutes: Number(e.target.value) })

                  }

                />

              </label>

            </div>

            <label className="checkbox"><input type="checkbox" checked={form.differentialIntervalHours>0} onChange={e=>setForm({...form,differentialIntervalHours:e.target.checked?4:0})}/>Agendar backups diferenciais</label>

            {form.differentialIntervalHours>0&&<label>Diferencial a cada (horas)<input type="number" required min={1} max={form.expectedIntervalHours-1} value={form.differentialIntervalHours} onChange={e=>setForm({...form,differentialIntervalHours:Number(e.target.value)})}/><small>Deve ser menor que o intervalo do completo. Exemplo: completo 24h e diferencial 4h.</small></label>}

            <label className="checkbox">

              <input

                type="checkbox"

                checked={form.enabled}
                disabled={!!edit&&deletionPending(edit)}

                onChange={(e) =>

                  setForm({ ...form, enabled: e.target.checked })

                }

              />

              Cliente ativo

            </label>

            <p className="form-hint">

              O agente mantém os dois horários, inclusive sem acesso à central. Antes do primeiro diferencial, ele gera e confirma um completo próprio. O diferencial acumula alterações desde esse completo.

            </p>

            {edit && (

              <div className="management-actions">

                <button

                  type="button"

                  className="secondary"

                  disabled={busy}

                  onClick={() =>

                    action(async () => {

                      const r = await api(

                        `/clients/${edit.id}/test-ftp`,

                        "POST",

                        {},

                      );

                      setNotice(r.message);

                      setModal(false);

                    })

                  }

                >

                  Testar FTP do cliente

                </button>

                <button type="button" className="text-button" onClick={()=>{setModal(false);setSettingsTab('sql');setSettingsOpen(true);}}>Configurar padrões compartilhados de instalação</button>

              </div>

            )}

            {error && <div className="alert error">{error}</div>}

            <div className="form-footer">

              {edit ? (userRole==='Admin'&&!edit.enabled&&<button type="button" className="danger-text" disabled={busy} onClick={()=>action(async()=>{const configured=await api('/storage');setSavedBaseDirectory(configured.baseDirectory);setModal(false);setDeleteClient(edit);})}>Excluir definitivamente</button>
              ) : (

                <button

                  type="button"

                  className="secondary"

                  onClick={() => setModal(false)}

                >

                  Cancelar

                </button>

              )}

              <button className="primary" disabled={busy}>

                Salvar cliente

              </button>

            </div>

          </form>

        </div>

      )}      {detail && (

        <div className="modal-backdrop">

          <section className="modal">

            <div className="modal-title">

              <h2>Detalhes da execução</h2>

              <button

                className="close"

                onClick={() => setDetail(null)}

                aria-label="Fechar"

              >

                ×

              </button>

            </div>

            <Badge status={detail.status} />

            <dl>

              <dt>Cliente</dt>

              <dd>{detail.clientName}</dd>

              <dt>Início / término</dt>

              <dd>

                {date(detail.startedAt)} → {date(detail.finishedAt)}

              </dd>

              <dt>Arquivo</dt>

              <dd className="wrap">{detail.status==='Skipped'?'Sem alterações — nenhum arquivo enviado':detail.fileName || "Não informado"}</dd>

              <dt>Tamanho reportado</dt>

              <dd>{bytes(detail.sizeBytes)}</dd>

            </dl>

            <h3>Mensagem do agente</h3>

            <pre className="wrap">

              {detail.message || "Sem mensagem adicional."}

            </pre>

            <p className="form-hint">

              Resultado declarado pelo agente. Esta versão não verifica

              fisicamente o arquivo nem executa restaurações.

            </p>

          </section>

        </div>

      )}

    </div>

  );

}

createRoot(document.getElementById("root")!).render(

  <React.StrictMode>

    <App />

  </React.StrictMode>,

);











