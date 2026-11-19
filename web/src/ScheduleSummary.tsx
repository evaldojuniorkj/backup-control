import { centralNow } from "./centralClock.ts";

import { configurationMatches,differentialBaseRecovered,telemetryFresh,type DashboardClient } from './dashboard';

import {agentDate,clockUncertain} from './communication';

const date=(at:string|null)=>at?new Date(at).toLocaleString('pt-BR'):'Não informado';

const phase:Record<string,string>={Created:'Preparando',SqlStarted:'Gerando backup SQL',BackupReady:'Verificando e compactando',ZipReady:'Enviando ao FTP',Uploaded:'Confirmando resultado'};

export function ScheduleSummary({client:c}:{client:DashboardClient}) {

  const t=c.agentTelemetry;const fresh=telemetryFresh(c);const applied=configurationMatches(c);

  function status(type:string,next:string|null){

    if(!c.enabled)return 'Cliente pausado';

    if(!t)return 'Aguardando relato do agente';

    if(!fresh)return 'Último relato — agente sem atualização recente';

    if(!applied)return 'Aguardando aplicação da configuração atual';

    if(!t.scheduleEnabled)return 'Agenda desativada no agente';

    if(clockUncertain(c))return 'Estimativa corrigida — sincronização do relógio necessária';

    if(t.currentBackupType===type)return phase[t.currentPhase||'']||'Em execução';

    if(type==='Differential'&&!t.hasFullBase)return 'Aguardando base completa';

    if(!next)return 'Aguardando horário do agente';

    const delay=centralNow()-Date.parse(next);

    return delay>c.graceMinutes*60000?'Prazo ultrapassado':delay>0?'Aguardando execução':'Programado';

  }

  return <div className="schedule-summary">

    <div><strong>Completo · {c.expectedIntervalHours}h</strong><span>Último backup enviado: {date(c.lastFullBackupAt)}</span><span>Última avaliação: {date(c.lastFullCheckAt||c.lastFullBackupAt)}</span><span>Próximo{clockUncertain(c)?' (estimado)':''}: {date(agentDate(t?.nextFullAt,c))}</span><small>{status('Full',t?.nextFullAt||null)}</small></div>

    <div><strong>Diferencial · {c.differentialIntervalHours>0?c.differentialIntervalHours+'h':'desativado'}</strong>{c.differentialIntervalHours>0&&<><span>Último backup enviado: {date(c.lastDifferentialBackupAt)}</span><span>Última avaliação: {date(c.lastDifferentialCheckAt||c.lastDifferentialBackupAt)}</span><span>Próximo{clockUncertain(c)?' (estimado)':''}: {date(agentDate(t?.nextDifferentialAt,c))}</span><small>{status('Differential',t?.nextDifferentialAt||null)}</small>{t?.hasFullBase&&<small>Base completa informada pelo agente{clockUncertain(c)?' (estimada)':''}: {date(agentDate(t.lastSuccessfulFullAt,c))}</small>}</>}</div>

    <DifferentialRecovery client={c}/><small className="schedule-report">{t?`${applied?'Agenda aplicada':'Agenda pendente'} · Agenda recebida em ${date(c.telemetryReceivedAt)}`:'Os próximos horários aparecerão quando o agente enviar a agenda.'}</small>

  </div>;

}

export function CompactSchedule({client:c}:{client:DashboardClient}){
 const t=c.agentTelemetry;
 return <div className="compact-schedule"><span><strong>Completo</strong> {date(agentDate(t?.nextFullAt,c))}</span>{c.differentialIntervalHours>0&&<span><strong>Diferencial</strong> {date(agentDate(t?.nextDifferentialAt,c))}</span>}<small>{!c.enabled?'Desativado':!t?'Aguardando agente':!telemetryFresh(c)?'Último relato, sem atualização':!configurationMatches(c)?'Agenda pendente':!t.scheduleEnabled?'Agenda desativada':clockUncertain(c)?'Horários estimados — confira o relógio':'Agenda aplicada'}</small></div>;
}


export function DifferentialRecovery({client:c}:{client:DashboardClient}) {
 if(!differentialBaseRecovered(c))return null;
 const fresh=telemetryFresh(c),t=c.agentTelemetry;
 return <div className="differential-recovery" role="status"><strong>Base recuperada por um completo novo</strong><span>Completo confirmado: {date(c.lastFullBackupAt)}. A falha diferencial anterior permanece no histórico.</span><span>Próximo diferencial{clockUncertain(c)?' (estimado)':''}: {date(agentDate(t?.nextDifferentialAt,c))}</span><small>{!fresh?'Horário do último relato; aguardando atualização do agente.':!configurationMatches(c)?'Configuração atual aguardando confirmação.':!t?.scheduleEnabled?'Agenda desativada no agente.':!t.nextDifferentialAt?'Aguardando horário informado pelo agente.':'Aguardando confirmação do primeiro diferencial após a recuperação.'}</small></div>;
}
