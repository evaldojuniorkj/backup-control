import {useEffect,useRef,useState} from 'react';
type Memory={totalBytes:number;usedBytes:number;availableBytes:number};
type Volume=Memory&{name:string;path:string;reservedBytes:number};
type Client={id:string;name:string;slug:string;enabled:boolean;allocatedBytes:number;fileBytes:number;fileCount:number;directoryExists:boolean|null;complete:boolean;skippedLinks:number};
type Metrics={observedAt:string;hostname:string;memory:Memory;volumes:Volume[];backupDirectory:string;usageObservedAt:string;hostStale:boolean;usageStale:boolean;usageComplete:boolean;storageMatches:boolean;clients:Client[];unassignedBytes:number;unassignedDirectories:number};
type Api=(path:string)=>Promise<any>;
const bytes=(n:number)=>{const units=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<units.length-1){n/=1024;i++;}return n.toLocaleString('pt-BR',{maximumFractionDigits:i?2:0})+' '+units[i];};
const date=(s:string)=>new Date(s).toLocaleString('pt-BR');
const percent=(used:number,total:number)=>total>0?Math.min(100,Math.max(0,used/total*100)):0;
function ResourceCard({title,resource,description}:{title:string;resource:Memory;description:string}){
 const used=percent(resource.usedBytes,resource.totalBytes);
 return <article className="server-resource"><h3>{title}</h3><strong>{bytes(resource.usedBytes)} <small>usados</small></strong><meter min={0} max={100} high={90} optimum={0} value={used} aria-label={`${title}: ${used.toFixed(1)}% usado`}/><div><span>{bytes(resource.availableBytes)} disponíveis</span><span>{used.toLocaleString('pt-BR',{maximumFractionDigits:1})}%</span></div><small>Total: {bytes(resource.totalBytes)}</small><p>{description}</p></article>;
}
export function ServerMonitor({api,onClose}:{api:Api;onClose:()=>void}){
 const [data,setData]=useState<Metrics|null>(null),[error,setError]=useState(''),[loading,setLoading]=useState(true),[query,setQuery]=useState(''),[status,setStatus]=useState('all'),[revision,setRevision]=useState(0);
 const previousFocus=useRef(document.activeElement);
 useEffect(()=>()=>{const e=previousFocus.current;if(e instanceof HTMLElement&&e.isConnected)e.focus();},[]);
 useEffect(()=>{let alive=true,inFlight=false;async function refresh(){if(inFlight)return;inFlight=true;try{const result=await api('/server/metrics');if(alive){setData(result);setError('');}}catch(e){if(alive)setError(e instanceof Error?e.message:'Não foi possível consultar o monitor.');}finally{inFlight=false;if(alive)setLoading(false);}}void refresh();const timer=setInterval(()=>void refresh(),30000);return()=>{alive=false;clearInterval(timer);};},[api,revision]);
 const backupVolume=data?.volumes.find(v=>v.name==='backups');
 const selected=data?.clients.filter(c=>(status==='all'||c.enabled===(status==='active'))&&(c.name+' '+c.slug).toLowerCase().includes(query.toLowerCase()))||[];
 const total=data?.clients.reduce((sum,c)=>sum+c.allocatedBytes,0)||0;
 return <div className="modal-backdrop" onClick={onClose}><section className="modal server-monitor" role="dialog" aria-modal="true" aria-labelledby="server-monitor-title" onClick={e=>e.stopPropagation()} onKeyDown={e=>{if(e.key==='Escape')onClose();if(e.key==='Tab'){const controls=e.currentTarget.querySelectorAll<HTMLElement>('button:not([disabled]),input,select');const first=controls[0],last=controls[controls.length-1];if(e.shiftKey&&document.activeElement===first){e.preventDefault();last?.focus();}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus();}}}}>
  <div className="modal-title"><div><span className="eyebrow">SERVIDOR DE BACKUPS</span><h2 id="server-monitor-title">Monitor do servidor</h2>{data&&<small>{data.hostname} · Coleta em {date(data.observedAt)}</small>}</div><button autoFocus className="close" aria-label="Fechar monitor" onClick={onClose}>×</button></div>
  <div className="server-monitor-toolbar"><p>RAM e disco do servidor Linux. Atualização automática a cada 30 segundos.</p><button className="secondary" onClick={()=>{setLoading(true);setRevision(x=>x+1);}} disabled={loading}>{loading?'Consultando…':'Atualizar métricas'}</button></div>
  {error&&<div className="alert error" role="alert">{error}{data&&' Exibindo a última amostra recebida.'}</div>}
  {!data&&loading&&<p role="status">Consultando o servidor…</p>}
  {data&&<>
   {data.hostStale&&<div className="alert error" role="alert">Coleta do servidor sem atualização há mais de 90 segundos. Os valores abaixo são da última amostra.</div>}
   <div className="server-resources"><ResourceCard title="Memória RAM" resource={data.memory} description="Usada = total menos memória disponível, incluindo cache reutilizável na disponibilidade."/>{data.volumes.map(v=><ResourceCard key={v.name} title={v.name==='system'?'Disco do sistema':'Disco dos backups'} resource={v} description={`${v.path}${v.reservedBytes>0?' · '+bytes(v.reservedBytes)+' reservados pelo sistema de arquivos':''}`}/>)}</div>
   {!data.storageMatches?<div className="alert error" role="alert">O coletor mede {data.backupDirectory}, mas o destino FTP configurado usa outro diretório. Ajuste o coletor antes de atribuir o consumo aos clientes.</div>:<>
    <div className="server-usage-heading"><div><h3>Espaço ocupado por cliente</h3><p>{bytes(total)} em diretórios cadastrados · Medição em {date(data.usageObservedAt)}</p><small>A leitura dos diretórios é feita a cada 5 minutos. A RAM do serviço FTP é compartilhada e não é atribuída a clientes.</small></div></div>
    {(!data.usageComplete||data.usageStale)&&<div className="alert error" role="alert">{data.usageStale?'Medição dos diretórios sem atualização recente. ':'Consulta parcial dos diretórios. '}Os tamanhos podem estar incompletos; não representam uma aferição completa atual.</div>}
    <div className="server-usage-filters"><label>Buscar cliente<input value={query} onChange={e=>setQuery(e.target.value)} placeholder="Nome ou identificador da pasta"/></label><label>Situação<select value={status} onChange={e=>setStatus(e.target.value)}><option value="all">Todos os clientes</option><option value="active">Ativos</option><option value="paused">Desativados</option></select></label></div>
    <div className="table-scroll"><table><thead><tr><th>Cliente / diretório</th><th>Espaço no disco</th><th>Arquivos</th><th>Do disco de backups</th><th>Medição</th></tr></thead><tbody>{selected.map(c=><tr key={c.id}><td><strong>{c.name}</strong><small>{data.backupDirectory}/{c.slug}/ · {c.enabled?'Ativo':'Desativado'}</small></td><td><strong>{bytes(c.allocatedBytes)}{!c.complete?' (parcial)':''}</strong><small>Tamanho dos arquivos: {bytes(c.fileBytes)}</small></td><td>{c.fileCount.toLocaleString('pt-BR')}</td><td>{backupVolume?percent(c.allocatedBytes,backupVolume.totalBytes).toLocaleString('pt-BR',{maximumFractionDigits:2})+'%':'—'}</td><td>{!c.complete?'Incompleta':c.directoryExists===false?'Pasta ainda não criada':'Concluída'}{c.skippedLinks>0&&<small>{c.skippedLinks} link(s) ignorado(s)</small>}</td></tr>)}</tbody></table>{selected.length===0&&<p className="empty">Nenhum cliente encontrado.</p>}</div>
    <p className="server-monitor-note">Outros arquivos ou pastas sem cadastro: <strong>{bytes(data.unassignedBytes)}</strong> ({data.unassignedDirectories} diretório(s)). O uso total do disco também inclui arquivos fora das pastas dos clientes e metadados do sistema de arquivos. Arquivos em envio podem mudar durante a coleta.</p>
   </>}
  </>}
 </section></div>;
}
