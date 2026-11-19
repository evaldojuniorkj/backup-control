import { centralNow } from "./centralClock.ts";

import {communicationReasons,type Communication} from './communication.ts';

export type Telemetry = {

  nextFullAt:string|null;nextDifferentialAt:string|null;lastSuccessfulFullAt?:string|null;hasFullBase:boolean;scheduleEnabled:boolean;

  appliedFullIntervalHours:number;appliedDifferentialIntervalHours:number;appliedDatabase:string;configAppliedAt:string|null;

  currentBackupType:string|null;currentPhase:string|null;currentStartedAt:string|null;

  currentOrigin?:string|null;

};

export type DashboardClient = {

  communication?:Communication|null;

  networkAlerts?:AttentionReason[];

  enabled:boolean;createdAt:string;lastSeen:string|null;telemetryReceivedAt:string|null;agentTelemetry:Telemetry|null;

  expectedIntervalHours:number;differentialIntervalHours:number;graceMinutes:number;database:string;

  lastFullBackupAt:string|null;lastDifferentialBackupAt:string|null;lastFullStatus:string|null;lastDifferentialStatus:string|null;

  lastStatus:string|null;lastBackupAt:string|null;

  lastFullCheckAt?:string|null;lastDifferentialCheckAt?:string|null;lastCheckAt?:string|null;lastCheckStatus?:string|null;

  lastFullAttemptAt?:string|null;lastDifferentialAttemptAt?:string|null;

  lastFullMessage?:string|null;lastDifferentialMessage?:string|null;

};

export type AttentionReason={code:string;message:string;since:string|null;detail?:string|null};

export const activeClients=<T extends {enabled:boolean}>(clients:T[])=>clients.filter(c=>c.enabled);

export const recent=(at:string|null,now=centralNow())=>!!at && now-Date.parse(at)<120000;

export const telemetryFresh=(c:DashboardClient,now=centralNow())=>!!c.agentTelemetry && recent(c.telemetryReceivedAt,now);

export function executionState(c:DashboardClient,now=centralNow()):'running'|'stale'|null {

  const t=c.agentTelemetry;

  if(!t?.currentBackupType&&!t?.currentPhase)return null;

  return t.currentBackupType&&t.currentPhase&&t.currentStartedAt&&telemetryFresh(c,now)&&recent(c.lastSeen,now)?'running':'stale';

}

export function waitingCommands<T extends {clientId:string;state:string;backupType:string}>(commands:T[],clients:(DashboardClient&{id:string})[],now=centralNow()):T[] {

  return commands.filter(command=>command.state==='Pending'||command.state==='Claimed'&&!clients.some(c=>c.id===command.clientId&&executionState(c,now)!==null&&c.agentTelemetry?.currentOrigin==='Manual'&&c.agentTelemetry.currentBackupType===command.backupType));

}

export const configurationMatches=(c:DashboardClient)=>!!c.agentTelemetry?.configAppliedAt

  && c.agentTelemetry.appliedDatabase===c.database && c.agentTelemetry.appliedFullIntervalHours===c.expectedIntervalHours

  && c.agentTelemetry.appliedDifferentialIntervalHours===c.differentialIntervalHours;

// Recovery applies only to the invalid differential-base failure, not unrelated send failures.
export function differentialBaseRecovered(c:DashboardClient):boolean {
 const failed=Date.parse(c.lastDifferentialAttemptAt||'');
 const full=Date.parse(c.lastFullBackupAt||'');
 return c.enabled&&c.differentialIntervalHours>0&&c.lastDifferentialStatus==='Failed'
  &&!!c.lastDifferentialMessage?.startsWith('Etapa BackupReady: A base diferencial mudou.')
  &&Number.isFinite(failed)&&Number.isFinite(full)&&full>failed
  &&c.lastFullStatus==='Succeeded'&&c.agentTelemetry?.hasFullBase===true;
}

export function attentionReasons(c:DashboardClient,now=centralNow()):AttentionReason[] {

  if(!c.enabled)return [];

  const reasons:AttentionReason[]=[...communicationReasons(c,now),...(c.networkAlerts||[])];

  const add=(code:string,message:string,since:string|null)=>reasons.push({code,message,since});

  for(const [status,message,at] of [[c.lastFullStatus,c.lastFullMessage,c.lastFullAttemptAt],[c.lastDifferentialStatus,c.lastDifferentialMessage,c.lastDifferentialAttemptAt]])if(status==='Failed'&&message?.startsWith('Etapa ZipReady:')){reasons.push({code:'ftp-connection',message:'Backup não enviado: falha na etapa de envio ao FTP',since:at||null,detail:message});break;}

  const t=c.agentTelemetry;

  const fresh=telemetryFresh(c,now);

  if(!c.lastSeen)add('never-connected','Agente ainda não conectado',c.createdAt);

  else if(!recent(c.lastSeen,now))add('offline','Agente sem contato há mais de 2 minutos',c.lastSeen);

  if(c.lastSeen && !t)add('no-telemetry','Agenda ainda não informada — agente precisa ser atualizado ou enviar seu primeiro relato',c.lastSeen);

  else if(t){

    if(!fresh)add('stale-telemetry','Agenda sem atualização recente — horários são do último relato',c.telemetryReceivedAt);

    if(!configurationMatches(c))add('config-pending','Configuração atual ainda não confirmada pelo agente',null);

    if(!t.scheduleEnabled)add('schedule-disabled','Rotina automática desativada no agente',c.telemetryReceivedAt);

    if(c.differentialIntervalHours>0&&!t.hasFullBase&&c.lastFullBackupAt)add('no-base','Diferencial aguardando nova base completa confirmada',c.telemetryReceivedAt);

    if(fresh&&t.currentStartedAt&&now-Date.parse(t.currentStartedAt)>3600000)add('long-running','Execução em andamento há mais de 1 hora — verificar',t.currentStartedAt);

  }

  if(!c.lastFullBackupAt)add('first-full','Primeiro completo ainda não confirmado',c.createdAt);

  for(const [kind,interval,last,status] of [

    ['full',c.expectedIntervalHours,[c.lastFullBackupAt,c.lastFullCheckAt].filter(Boolean).sort().at(-1)||null,c.lastFullStatus],

    ['diff',c.differentialIntervalHours,[c.lastFullBackupAt,c.lastDifferentialBackupAt,c.lastFullCheckAt,c.lastDifferentialCheckAt].filter(Boolean).sort().at(-1)||null,c.lastDifferentialStatus],

  ] as const){

    if(interval<=0)continue;

    const label=kind==='full'?'Completo':'Diferencial';

    if((status==='Failed'||status==='Warning')&&!(kind==='diff'&&differentialBaseRecovered(c)))reasons.push({code:kind+'-failed',message:`${label}: ${status==='Failed'?'última tentativa falhou':'última tentativa terminou com avisos'}`,since:kind==='full'?c.lastFullAttemptAt||null:c.lastDifferentialAttemptAt||null,detail:kind==='full'?c.lastFullMessage:c.lastDifferentialMessage});

    const deadline=Date.parse(last||c.createdAt)+(interval*60+c.graceMinutes)*60000;

    const running=fresh&&t?.currentBackupType===(kind==='full'?'Full':'Differential');

    if(now>deadline&&!running)add(kind+'-overdue',`${label} atrasado — intervalo e tolerância ultrapassados`,new Date(deadline).toISOString());

  }

  if((c.lastStatus==='Failed'||c.lastStatus==='Warning') && !reasons.some(r=>r.code.endsWith('-failed')))add('failed',c.lastStatus==='Failed'?'Última execução falhou':'Última execução terminou com avisos',c.lastBackupAt);

  return reasons;

}

