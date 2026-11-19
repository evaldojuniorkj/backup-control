export type Registration={registrationId:string;status:string;machineName:string;requestedName:string;instanceId:string;expiresAt:string;existingClientId?:string|null};
export type NewClient={name:string;slug:string;database:string;expectedIntervalHours:number;differentialIntervalHours:number;graceMinutes:number};
export function registrationStatus(request:{status:string;expiresAt:string}):string{return request.status;}
export function eligibleExistingClients<T extends {id:string;enabled:boolean;hasAgentBinding?:boolean;machineName?:string}>(clients:T[],request:{existingClientId?:string|null}):T[]{return clients.filter(c=>c.enabled&&(c.id===request.existingClientId||c.hasAgentBinding===false));}
export function approvalPayload(request:{status:string;expiresAt:string},code:string,mode:string,clientId:string,form:Partial<NewClient>){
 if(registrationStatus(request)!=='Pending')throw new Error('Esta solicitação não está mais pendente. Atualize a lista.');
 const confirmationCode=code.trim().toUpperCase().replace(/[-\s]/g,'');if(!/^[A-F0-9]{10}$/.test(confirmationCode))throw new Error('Digite o código de 10 caracteres exibido na máquina do cliente.');
 if(mode==='existing'){if(!clientId)throw new Error('Selecione o cliente que receberá esta máquina.');return {confirmationCode,clientId};}
 if(mode!=='new')throw new Error('Selecione como cadastrar a máquina.');
 const client={name:(form.name||'').trim(),slug:(form.slug||'').trim(),database:(form.database||'').trim(),expectedIntervalHours:Number(form.expectedIntervalHours),differentialIntervalHours:Number(form.differentialIntervalHours),graceMinutes:Number(form.graceMinutes),enabled:true};
 if(!client.name||client.name.length>120||!/^[a-z0-9][a-z0-9_-]{1,62}$/.test(client.slug)||!client.database||client.database.length>128||![client.expectedIntervalHours,client.differentialIntervalHours,client.graceMinutes].every(Number.isInteger)||client.expectedIntervalHours<1||client.expectedIntervalHours>8760||client.differentialIntervalHours<0||client.differentialIntervalHours>=client.expectedIntervalHours||client.graceMinutes<0||client.graceMinutes>10080)throw new Error('Confira nome, identificador, banco e intervalos. O diferencial deve ser menor que o completo.');
 return {confirmationCode,client};
}
