import { centralNow } from "./centralClock.ts";
export type Communication={clockSkewSeconds?:number|null;clockSampledAt?:string|null;pendingResults?:number|null;lastResultError?:string|null;lastResultRejectedAt?:string|null;sqlRevision?:string|null;appliedSqlRevision?:string|null;sqlConfigStatus?:string|null;sqlConfigError?:string|null;requiresAgentUpdate?:boolean};
type ClientCommunication={communication?:Communication|null;telemetryReceivedAt?:string|null};
export type CommunicationReason={code:string;message:string;since:string|null;detail?:string|null};
export const clockUncertain=(c:ClientCommunication)=>Math.abs(c.communication?.clockSkewSeconds||0)>300;
export function agentDate(value:string|null|undefined,c:ClientCommunication):string|null {
 if(!value)return null;const time=Date.parse(value);return Number.isFinite(time)&&clockUncertain(c)?new Date(time-(c.communication?.clockSkewSeconds||0)*1000).toISOString():value;
}
export function reportTimeLabel(quality?:string|null){return quality==='ClockAdjusted'?'Horário estimado, corrigido pela diferença observada entre os relógios':quality==='ReceiptEstimated'?'Horário estimado pelo recebimento na central':'Horário informado pelo agente';}
export function communicationReasons(c:ClientCommunication,now=centralNow()):CommunicationReason[]{
 const x=c.communication;if(!x)return [];const list:CommunicationReason[]=[];
 if(clockUncertain(c)){const minutes=Math.round(Math.abs(x.clockSkewSeconds||0)/60);const sample=x.clockSampledAt||null;const stale=!sample||now-Date.parse(sample)>300000;list.push({code:'clock-skew',message:`${stale?'Último desvio observado: relógio da máquina':'Relógio da máquina'} ${x.clockSkewSeconds!>0?'adiantado':'atrasado'} em aproximadamente ${minutes} min — sincronize o Windows`,since:sample});}
 if((x.pendingResults||0)>0)list.push({code:'pending-results',message:`${x.pendingResults} resultado(s) aguardando confirmação da central`,since:x.lastResultRejectedAt||null});
 if(x.lastResultError)list.push({code:'result-error',message:'Falha na confirmação de resultado — o agente preserva o relato para reenvio',since:x.lastResultRejectedAt||null});
 const status=(x.sqlConfigStatus||'').toLowerCase();
 if(status.includes('fail')||status.includes('reject')||status.includes('error'))list.push({code:'sql-config',message:'Atualização SQL recusada pelo agente — a conexão anterior foi preservada',since:c.telemetryReceivedAt||null});
 else if(x.sqlRevision&&x.appliedSqlRevision!==x.sqlRevision&&!x.requiresAgentUpdate)list.push({code:'sql-pending',message:'Nova configuração SQL aguardando aplicação pelo agente',since:null});
 if(x.requiresAgentUpdate)list.push({code:'agent-update',message:'Atualização inicial do agente necessária para receber alterações SQL pela API',since:null});
 return list;
}
