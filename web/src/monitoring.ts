import {attentionReasons,differentialBaseRecovered,executionState,recent,type DashboardClient} from './dashboard.ts';
import {centralNow} from './centralClock.ts';
export type MonitorFilter='all'|'problems'|'running'|'healthy';
export function monitorSignal(c:DashboardClient&{status?:string},now=centralNow()){
 if(!c.enabled)return {tone:'paused',label:'PAUSADO',detail:'Rotinas desativadas'};
 const state=executionState(c,now);
 if(state==='running'){const phases:Record<string,string>={Created:'Preparando backup',SqlStarted:'Gerando backup SQL',BackupReady:'Verificando e compactando',ZipReady:'Enviando ao FTP',Uploaded:'Confirmando resultado'};return {tone:'running',label:'EM EXECUÇÃO',detail:phases[c.agentTelemetry?.currentPhase||'']||'Backup em andamento'};}
 if(c.lastStatus==='Failed'||c.status==='Failed')return {tone:'failed',label:'FALHA',detail:'O último backup não foi concluído'};
 if(!recent(c.lastSeen,now))return {tone:'offline',label:c.lastSeen?'SEM CONTATO':'SEM AGENTE',detail:c.lastSeen?'Aguardando comunicação da máquina':'Instalação ainda não confirmada'};
 const reasons=attentionReasons(c,now);
 if(state==='stale'||reasons.length)return {tone:'attention',label:'ATENÇÃO',detail:reasons[0]?.message||'Execução sem atualização'};
 if(differentialBaseRecovered(c))return {tone:'healthy',label:'BASE RECUPERADA',detail:'Completo novo confirmado; aguardando o próximo diferencial'};
 if(c.lastStatus==='Succeeded'||c.status==='Succeeded')return {tone:'healthy',label:'SUCESSO',detail:'Último envio confirmado'};
 return {tone:'idle',label:'AGUARDANDO',detail:'Aguardando primeiro resultado'};
}
export function matchesMonitor(c:DashboardClient&{status?:string},filter:MonitorFilter,now=centralNow()){
 const s=monitorSignal(c,now);
 return filter==='all'||filter==='running'&&s.tone==='running'||filter==='healthy'&&backupsUpToDate(c,now)||filter==='problems'&&(attentionReasons(c,now).length>0||['failed','offline','attention'].includes(s.tone));
}

export function monitorPriority(c:DashboardClient&{status?:string},now=centralNow()):number {
 return ({failed:0,running:1,attention:2,offline:3,idle:4,healthy:5,paused:6} as Record<string,number>)[monitorSignal(c,now).tone]??7;
}

// A current execution cannot hide a deadline already missed by prior backups.
export function backupsUpToDate(c:DashboardClient,now=centralNow()):boolean {
 if(!c.enabled||!c.lastFullBackupAt||attentionReasons(c,now).length>0)return false;
 for(const [interval,dates] of [
  [c.expectedIntervalHours,[c.lastFullBackupAt,c.lastFullCheckAt]],
  [c.differentialIntervalHours,[c.lastFullBackupAt,c.lastDifferentialBackupAt,c.lastFullCheckAt,c.lastDifferentialCheckAt]],
 ] as const){
  if(interval<=0)continue;
  const latest=Math.max(...dates.filter((at):at is string=>!!at).map(Date.parse));
  if(!Number.isFinite(latest)||now>latest+(interval*60+c.graceMinutes)*60000)return false;
 }
 return true;
}
