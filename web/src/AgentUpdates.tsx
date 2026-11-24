import {useCallback,useEffect,useRef,useState} from 'react';
import {
 activeUpdate,clientUpdateStatus,isOutdatedAgent,newlyConfirmedUpdates,pendingUpdate,updateReason,
 updateRequestNotice,type UpdateClient,type UpdateJob
} from './updates';

type Api=(path:string,method?:string,body?:unknown)=>Promise<any>;
type UpdateData={release:{version:string|null;available:boolean};jobs:UpdateJob[]};
const labels:Record<string,string>={
 Pending:'Pendente',Waiting:'Aguardando agente ocioso',Downloading:'Baixando',Applying:'Aplicando',
 Verifying:'Verificando versão',Failed:'Falhou',RolledBack:'Versão anterior restaurada'
};

export function AgentUpdates({clients,api,isAdmin,open,onClose}:{
 clients:UpdateClient[];api:Api;isAdmin:boolean;open:boolean;onClose:()=>void;
}){
 const [data,setData]=useState<UpdateData|null>(null);
 const [selected,setSelected]=useState<string[]>([]);
 const [busy,setBusy]=useState(false);
 const [error,setError]=useState('');
 const [requestNotice,setRequestNotice]=useState<{message:string;hasRejections:boolean}|null>(null);
 const [watching,setWatching]=useState(false);
 const [confirmed,setConfirmed]=useState<UpdateJob[]>([]);
 const tracked=useRef(new Set<string>());
 const refreshing=useRef<Promise<void>|null>(null);

 const refresh=useCallback(()=>{
  if(refreshing.current)return refreshing.current;
  refreshing.current=api('/updates')
   .then((next:UpdateData)=>{
    const completed=newlyConfirmedUpdates(next.jobs,tracked.current);
    tracked.current=new Set(next.jobs.filter(job=>activeUpdate(job.state)).map(job=>job.id));
    setWatching(tracked.current.size>0);
    if(completed.length){
     setConfirmed(previous=>[...previous,...completed]);
     setRequestNotice(previous=>previous?.hasRejections?previous:null);
    }
    setData(next);
    setError('');
   })
   .catch(error=>setError(error instanceof Error?error.message:'Falha ao consultar atualizações.'))
   .finally(()=>{refreshing.current=null;});
  return refreshing.current;
 },[api]);

 useEffect(()=>{
  if(!open)return;
  const previous=document.activeElement as HTMLElement|null;
  void refresh();
  return()=>previous?.focus();
 },[open,refresh]);
 useEffect(()=>{
  if(!open&&!watching)return;
  const timer=setInterval(()=>void refresh(),10000);
  return()=>clearInterval(timer);
 },[open,watching,refresh]);
 useEffect(()=>{
  if(!confirmed.length)return;
  const timer=setTimeout(()=>setConfirmed([]),10000);
  return()=>clearTimeout(timer);
 },[confirmed]);

 async function request(ids:string[]){
  setBusy(true);
  try{
   const result=await api('/updates','POST',{clientIds:ids});
   // Finish a GET started before the POST before tracking the newly registered jobs.
   await refreshing.current;
   for(const job of result.accepted??[])tracked.current.add(job.id);
   setWatching(tracked.current.size>0);
   setSelected([]);
   setRequestNotice({message:updateRequestNotice(result,clients),hasRejections:!!result.rejected?.length});
   await refresh();
  }catch(error){setError(error instanceof Error?error.message:'Falha ao solicitar atualização.');}
  finally{setBusy(false);}
 }
 async function cancel(id:string){
  setBusy(true);
  try{await api('/updates/'+id+'/cancel','POST',{});await refreshing.current;await refresh();}
  catch(error){setError(error instanceof Error?error.message:'Falha ao cancelar atualização.');}
  finally{setBusy(false);}
 }

 const notification=confirmed.length>0&&<div className="alert success agent-update-notification">
  <span role="status">{confirmed.length===1
   ?`${confirmed[0].clientName} atualizado para a versão ${confirmed[0].targetVersion}.`
   :`${confirmed.length} agentes atualizados com sucesso.`}</span>
  <button type="button" aria-label="Dispensar confirmação de atualização" onClick={()=>setConfirmed([])}>×</button>
 </div>;
 if(!open)return notification||null;

 const version=data?.release.version??'';
 const rows=data?clients.flatMap(client=>{
  const {job,installedVersion}=clientUpdateStatus(data.jobs,client.id,version);
  const current={...client,agentVersion:installedVersion??client.agentVersion};
  const outdated=isOutdatedAgent(current.agentVersion,version);
  if(!outdated&&!(job&&activeUpdate(job.state)))return [];
  return [{client:current,job,outdated,reason:updateReason(current,version,data.release.available,data.jobs)}];
 }):[];
 const eligible=rows.filter(row=>!row.reason).map(row=>row.client.id);
 const ids=selected.filter(id=>eligible.includes(id));
 const outdatedCount=rows.filter(row=>row.outdated).length;

 return <div className="modal-backdrop" onClick={onClose}>
  <section className="modal client-operations-modal" role="dialog" aria-modal="true" aria-labelledby="updates-title"
   onClick={e=>e.stopPropagation()} onKeyDown={e=>{
    if(e.key==='Escape')onClose();
    if(e.key==='Tab'){
     const items=e.currentTarget.querySelectorAll<HTMLElement>('button:not([disabled]),input:not([disabled])');
     const first=items[0],last=items[items.length-1];
     if(e.shiftKey&&document.activeElement===first){e.preventDefault();last?.focus();}
     else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus();}
    }
   }}>
   <div className="modal-title">
    <div>
     <h2 id="updates-title">Atualizações remotas</h2>
     <p>{data?`Versão disponível · ${version||'Não publicada'}${data.release.available?'':' · pacote indisponível'}`:'Consultando versão…'}</p>
    </div>
    <button autoFocus className="close" aria-label="Fechar atualizações" onClick={onClose}>×</button>
   </div>
   {notification}
   <p className="alert warning">Agentes antigos, incluindo a versão 0.6, precisam de uma reinstalação inicial para habilitar a atualização remota. A atualização aguarda os backups em curso e os resultados pendentes.</p>
   {!isAdmin&&<p>Somente administradores podem solicitar ou cancelar atualizações.</p>}
   {error&&<p className="alert error" role="alert">{error}</p>}
   {requestNotice&&<p role="status">{requestNotice.message}</p>}
   {data&&<>
    <div className="update-toolbar">
     <button className="secondary" disabled={!isAdmin||busy||!eligible.length} onClick={()=>setSelected(eligible)}>Selecionar elegíveis</button>
     <button disabled={!isAdmin||busy||!ids.length} onClick={()=>void request(ids)}>Atualizar selecionados ({ids.length})</button>
     <button className="secondary" disabled={busy} onClick={()=>void refresh()}>Consultar estados</button>
    </div>
    <p>{outdatedCount?`${outdatedCount} agente(s) desatualizado(s).`:'Nenhum agente com versão anterior à versão publicada.'}</p>
    {rows.length>0&&<div className="table-scroll">
     <table aria-label="Agentes e atualizações em andamento">
      <thead><tr><th scope="col">Selecionar</th><th scope="col">Cliente</th><th scope="col">Instalada</th><th scope="col">Disponibilidade / estado</th><th scope="col">Ação</th></tr></thead>
      <tbody>{rows.map(({client,job,reason})=><tr key={client.id}>
       <td><input type="checkbox" aria-label={`Selecionar ${client.name}`} disabled={!isAdmin||busy||!!reason}
        checked={ids.includes(client.id)} onChange={e=>setSelected(e.target.checked?[...selected,client.id]:selected.filter(id=>id!==client.id))}/></td>
       <td>{client.name}</td>
       <td>{client.agentVersion||'Não informada'}</td>
       <td>{job?<><span className={`badge ${job.state.toLowerCase()}`}>{labels[job.state]||job.state}</span>
        <small>{job.message}</small></>:reason||'Atualização remota habilitada'}</td>
       <td>{isAdmin&&job&&pendingUpdate(job.state)
        ?<button className="danger-text" disabled={busy} onClick={()=>void cancel(job.id)}>Cancelar atualização</button>
        :<button disabled={!isAdmin||busy||!!reason} onClick={()=>void request([client.id])}>Atualizar agente</button>}</td>
      </tr>)}</tbody>
     </table>
    </div>}
   </>}
  </section>
 </div>;
}
