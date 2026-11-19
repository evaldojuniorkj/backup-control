export type UpdateClient={id:string;name:string;enabled:boolean;agentVersion?:string|null;remoteUpdateSupported?:boolean|null;hasAgentBinding?:boolean};
export type UpdateJob={id:string;clientId:string;clientName:string;state:string;targetVersion:string;updatedAt:string;message:string;installedVersion?:string};
export const pendingUpdate=(state:string)=>['Pending','Waiting'].includes(state);
export function updateReason(c:UpdateClient,version:string,available:boolean,jobs:UpdateJob[]){
 if(!c.enabled)return 'Cliente desativado';if(!c.hasAgentBinding)return 'Agente não vinculado';if(c.remoteUpdateSupported!==true)return 'Reinstalação inicial necessária';if(!available)return 'Pacote indisponível';if(!c.agentVersion)return 'Versão não informada';
 const a=c.agentVersion.split('.').map(Number),b=version.split('.').map(Number);if(a.some(Number.isNaN)||b.some(Number.isNaN))return 'Versão não reconhecida';let older=false;for(let i=0;i<Math.max(a.length,b.length);i++){if((a[i]||0)<(b[i]||0)){older=true;break;}if((a[i]||0)>(b[i]||0))break;}if(!older)return 'Versão atual ou mais recente';
 if(jobs.some(j=>j.clientId===c.id&&['Pending','Waiting','Downloading','Applying','Verifying'].includes(j.state)))return 'Atualização em andamento';return '';
}
export const clientFtpPath=(base:string,slug:string)=>base.replace(/\/+$/,'')+'/'+slug+'/';
export const canDeleteClient=(slug:string,confirmation:string,enabled:boolean=false)=>!enabled&&!!slug&&slug===confirmation;
export type DeactivationState='WaitingForAgent'|'RequiresUpgrade'|'Confirmed'|null;
export type DeleteResult={pending?:boolean;state?:DeactivationState;message?:string};
export const deletionPending=(client:{deletionPending?:boolean;status?:string;deactivationState?:DeactivationState})=>!!client.deletionPending||client.status==='deletion_pending'||!!client.deactivationState;
export function deactivationNotice(state:DeactivationState|undefined){
 if(state==='RequiresUpgrade')return 'Exclusão pendente: esta versão antiga não suporta a desativação remota nem a atualização remota necessária. Reinstalação inicial do agente necessária; backups e histórico serão preservados até a confirmação.';
 if(state==='Confirmed')return 'Desativação permanente confirmada pelo agente. Aguardando a exclusão dos backups no FTP e do histórico pela central.';
 return 'Exclusão pendente: aguardando o agente confirmar a desativação permanente e a remoção das credenciais SQL/FTP. Se a máquina estiver offline, a central aguardará sua reconexão. Agentes compatíveis com atualização remota receberão a versão necessária automaticamente quando o pacote estiver disponível. Backups no FTP e histórico serão preservados até a confirmação.';
}
export const deletionResultNotice=(result:DeleteResult|undefined)=>result?.pending?deactivationNotice(result.state):'Cliente, backups e histórico excluídos.';

export function updateRequestNotice(result:{accepted?:unknown[];rejected?:{clientId:string;error?:string;reason?:string}[]},clients:{id:string;name:string}[]){const accepted=result.accepted?.length||0;const rejected=result.rejected||[];return accepted+' solicitação(ões) registrada(s).'+(rejected.length?' Rejeições: '+rejected.map(r=>(clients.find(c=>c.id===r.clientId)?.name||r.clientId)+': '+(r.error||r.reason||'Solicitação recusada')).join('; '):' Acompanhe os estados abaixo.');}

export function isOutdatedAgent(installed:string|null|undefined,current:string):boolean {
 if(!installed||!/^\d+(?:\.\d+){1,3}$/.test(installed)||!/^\d+(?:\.\d+){1,3}$/.test(current))return false;
 const a=installed.split('.').map(Number),b=current.split('.').map(Number);
 for(let i=0;i<Math.max(a.length,b.length);i++){if((a[i]||0)!==(b[i]||0))return (a[i]||0)<(b[i]||0);}
 return false;
}
