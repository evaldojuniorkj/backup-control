import { useEffect, useState } from "react";

type Client = { id: string; name: string; slug: string };
type BackupFile = { fileName: string; sizeBytes: number | null; backupType: string; backupAt: string | null; modifiedAt: string | null; dateSource: string; status: string; hasHistory: boolean };
type Catalog = { clientId: string; clientName: string; remoteDirectory: string; directoryExists: boolean; checkedAt: string; files: BackupFile[] };
const types: Record<string,string> = {Full:"Completo",Differential:"Diferencial",Log:"Log",Unknown:"Não identificado"};
const date = (value: string | null) => value ? new Date(value).toLocaleString("pt-BR") : "Não identificada";
const size = (value: number | null) => value === null ? "—" : value < 1048576 ? `${(value/1024).toLocaleString("pt-BR",{maximumFractionDigits:1})} KB` : `${(value/1048576).toLocaleString("pt-BR",{maximumFractionDigits:1})} MB`;

export function BackupCatalog({clients,initialClientId,api,lockedClient=false}:{clients:Client[];initialClientId:string;api:(path:string)=>Promise<Catalog>;lockedClient?:boolean}) {
  const [clientId,setClientId]=useState(initialClientId || clients[0]?.id || "");
  const [catalog,setCatalog]=useState<Catalog|null>(null);
  const [loading,setLoading]=useState(false);
  const [error,setError]=useState("");
  const [revision,setRevision]=useState(0);
  const [query,setQuery]=useState("");
  const [type,setType]=useState("");
  const [from,setFrom]=useState("");
  const [to,setTo]=useState("");
  const [page,setPage]=useState(1);
  useEffect(()=>{
    if(!clientId && clients[0]) setClientId(clients[0].id);
  },[clients,clientId]);
  useEffect(()=>{
    if(!clientId)return;
    let active=true;
    setLoading(true);setError("");setCatalog(null);
    api(`/clients/${clientId}/files`).then(result=>{if(active)setCatalog(result);})
      .catch(e=>{if(active)setError(e.message);}).finally(()=>{if(active)setLoading(false);});
    return ()=>{active=false;};
  },[clientId,revision]);
  useEffect(()=>setPage(1),[clientId,query,type,from,to,revision]);
  const files=(catalog?.files||[]).filter(file=>{
    const value=file.backupAt ? new Date(file.backupAt) : null;
    const day=value ? `${value.getFullYear()}-${String(value.getMonth()+1).padStart(2,"0")}-${String(value.getDate()).padStart(2,"0")}` : "";
    return file.fileName.toLowerCase().includes(query.toLowerCase()) && (!type||file.backupType===type)
      && (!from || !!day && day>=from) && (!to || !!day && day<=to);
  });
  const count=Math.max(1,Math.ceil(files.length/20));
  const currentPage=Math.min(page,count);
  return <section className="panel ftp-catalog">
    <div className="panel-heading"><div><h2>Arquivos no FTP</h2><p>Consulta real à pasta do cliente. Datas e horas no fuso deste navegador.</p></div><button className="secondary" disabled={loading||!clientId} onClick={()=>setRevision(x=>x+1)}>{loading?"Consultando…":"Atualizar arquivos"}</button></div>
    <div className="catalog-filters">
      {!lockedClient&&<label>Cliente<select value={clientId} onChange={e=>setClientId(e.target.value)}><option value="" disabled>Selecione um cliente</option>{clients.map(c=><option key={c.id} value={c.id}>{c.name} · {c.slug}</option>)}</select></label>}
      <label>Buscar arquivo<input value={query} onChange={e=>setQuery(e.target.value)} placeholder="Nome do arquivo"/></label>
      <label>Tipo<select value={type} onChange={e=>setType(e.target.value)}><option value="">Todos os tipos</option>{Object.entries(types).map(([key,label])=><option key={key} value={key}>{label}</option>)}</select></label>
      <label>De<input type="date" value={from} onChange={e=>setFrom(e.target.value)}/></label>
      <label>Até<input type="date" min={from||undefined} value={to} onChange={e=>setTo(e.target.value)}/></label>
    </div>
    {!clients.length && <div className="empty"><h3>Cadastre um cliente primeiro</h3><p>Cada cadastro corresponde à sua própria pasta de backups.</p></div>}
    {error && <div className="alert error" role="alert">{error}</div>}
    {loading && <div className="empty" role="status">Consultando a pasta no servidor FTP…</div>}
    {catalog && <>
      <div className="catalog-summary"><div><strong>{catalog.clientName}</strong><small>{catalog.remoteDirectory}/</small></div><span>{files.length} arquivo(s) · Conferido em {date(catalog.checkedAt)}</span></div>
      {!files.length ? <div className="empty"><h3>{!catalog.directoryExists?"Pasta ainda não criada":catalog.files.length?"Nenhum arquivo corresponde aos filtros":"Nenhum backup encontrado"}</h3><p>{!catalog.directoryExists?"O agente cria a pasta durante o teste de conexão ou o envio do primeiro backup.":"Somente arquivos ZIP de backup e envios em andamento são exibidos."}</p></div> : <div className="table-scroll"><table><thead><tr><th>Arquivo / cliente</th><th>Início do backup</th><th>Tipo</th><th>Tamanho no FTP</th><th>Situação</th><th/></tr></thead><tbody>
        {files.slice((currentPage-1)*20,currentPage*20).map(file=><tr key={file.fileName}>
          <td className="catalog-filename"><strong>{file.fileName}</strong><small>{catalog.clientName}{!file.hasHistory?" · Sem histórico associado":""}</small></td>
          <td>{date(file.backupAt)}<small>{file.dateSource==="Execution"?"Início registrado da execução":file.dateSource==="FileName"?"Data extraída do nome (UTC)":"Data do backup desconhecida"}</small><small>Modificado no FTP: {file.modifiedAt?date(file.modifiedAt):"Não informada"}</small></td>
          <td>{types[file.backupType]||types.Unknown}</td><td>{size(file.sizeBytes)}<small>{file.sizeBytes?.toLocaleString("pt-BR")} bytes</small></td>
          <td><span className={`badge ${file.status==="Available"?"succeeded":"neutral"}`}>{file.status==="Available"?"Disponível":"Enviando"}</span></td>
          <td>{file.status==="Available"?<a className="small-button catalog-download" href={`/api/clients/${encodeURIComponent(catalog.clientId)}/files/download?fileName=${encodeURIComponent(file.fileName)}`} download>Baixar</a>:<span className="muted">Aguarde o envio</span>}</td>
        </tr>)}
      </tbody></table></div>}
      {files.length>20&&<div className="catalog-pagination"><button className="secondary" disabled={currentPage<=1} onClick={()=>setPage(currentPage-1)}>Anterior</button><span>Página {currentPage} de {count}</span><button className="secondary" disabled={currentPage>=count} onClick={()=>setPage(currentPage+1)}>Próxima</button></div>}
      <p className="form-hint">Disponível significa encontrado nesta consulta, sem validação de integridade. Para restaurar um diferencial, preserve também o completo correspondente. Não há exclusão de arquivos nesta tela.</p>
    </>}
  </section>;
}
