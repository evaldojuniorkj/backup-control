import { executionState,waitingCommands,type DashboardClient } from './dashboard';

import { agentDate,clockUncertain } from './communication';

import { CommunicationAlerts } from './ClientOperations';

type Client=DashboardClient&{id:string;name:string;machineName:string};

const types:Record<string,string>={Full:'Completo',Differential:'Diferencial',Log:'Log'};

const origins:Record<string,string>={Manual:'Manual',Scheduled:'Agendado',Unknown:'Não informada'};

const phases:Record<string,string>={Created:'Preparando backup',SqlStarted:'Gerando backup no SQL Server',BackupReady:'Verificando e compactando',ZipReady:'Enviando ao FTP',Uploaded:'Confirmando resultado'};

const date=(s:string|null)=>s?new Date(s).toLocaleString('pt-BR'):'Não informado';

type Command={id:string;clientId:string;clientName:string;backupType:string;state:string;createdAt:string};

export function RunningBackups({clients,commands,onClose}:{clients:Client[];commands:Command[];onClose:()=>void}) {

  const running=clients.filter(c=>executionState(c)==='running');

  const stale=clients.filter(c=>executionState(c)==='stale');

  const waiting=waitingCommands(commands,clients);

  function row(c:Client,isStale:boolean){

    const t=c.agentTelemetry!;

    return <article key={c.id} className="attention-client execution-client">

      <div className="attention-client-heading"><div><h3>{c.name}</h3><small>{c.machineName||'Máquina não informada'} · {c.database}</small></div><span className={`badge ${isStale?'warning':'claimed'}`}>{isStale?'Sem atualização':'Em execução'}</span></div>

      <CommunicationAlerts client={c}/>

      <dl className="execution-details">
        <div><dt>Tipo</dt><dd>{types[t.currentBackupType||'']||'Não informado'}</dd></div>
        <div><dt>Origem</dt><dd>{origins[t.currentOrigin||'Unknown']||'Não informada'}</dd></div>
        <div><dt>Início{clockUncertain(c)?' (estimado)':''}</dt><dd>{date(agentDate(t.currentStartedAt,c))}</dd></div>
        <div><dt>{isStale?'Última etapa informada':'Etapa informada pelo agente'}</dt><dd>{phases[t.currentPhase||'']||'Não informada'}</dd></div>
        <div><dt>Última atualização</dt><dd>{date(c.telemetryReceivedAt)}</dd></div>
      </dl>

      {isStale&&<p className="form-hint">O relato está desatualizado ou incompleto. Não é possível confirmar se o backup continua em execução.</p>}

    </article>;

  }

  return <div className="modal-backdrop" onClick={onClose}><section className="modal attention-modal" role="dialog" aria-modal="true" aria-labelledby="running-title" onClick={e=>e.stopPropagation()} onKeyDown={e=>{if(e.key==='Escape')onClose();if(e.key==='Tab'){e.preventDefault();e.currentTarget.querySelector<HTMLButtonElement>('button')?.focus();}}}>

    <div className="modal-title"><div><span className="eyebrow">ACOMPANHAMENTO DA OPERAÇÃO</span><h2 id="running-title">Atividade dos backups</h2></div><button autoFocus className="close" aria-label="Fechar atividade dos backups" onClick={onClose}>×</button></div>

    {!running.length && !waiting.length && !stale.length &&
      <p>Nenhuma atividade de backup no momento.</p>}

    {running.length > 0 && <>
      <h3 className="stale-heading">Executando agora ({running.length})</h3>
      {running.map(c=>row(c,false))}
    </>}

    {waiting.length > 0 && <>
      <h3 className="stale-heading">Aguardando início ({waiting.length})</h3>
      {waiting.map(command=>{
        const client=clients.find(c=>c.id===command.clientId);
        return <article key={command.id} className="attention-client">
          <div className="attention-client-heading">
            <h3>{command.clientName}</h3>
            <span className="badge pending">Aguardando início</span>
          </div>
          {client&&<CommunicationAlerts client={client}/>}
          <dl className="execution-details">
            <div><dt>Tipo</dt><dd>{types[command.backupType]||command.backupType}</dd></div>
            <div><dt>Origem</dt><dd>Manual</dd></div>
            <div><dt>Solicitado em</dt><dd>{date(command.createdAt)}</dd></div>
            <div><dt>Situação</dt><dd>{command.state==='Pending'?'Aguardando o agente receber':'Recebido pelo agente — aguardando confirmação de início'}</dd></div>
          </dl>
        </article>;
      })}
    </>}

    {stale.length > 0 && <>
      <h3 className="stale-heading">Sem atualização ({stale.length})</h3>
      {stale.map(c=>row(c,true))}
    </>}

  </section></div>;

}

