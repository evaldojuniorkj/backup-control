import React, { useEffect, useState, type FormEvent } from 'react';
import { centralOrigin } from './installation';
type Api = (path: string, method?: string, body?: unknown) => Promise<any>;
type Settings = { centralUrl: string; sqlServer: string; sqlUsername: string; hasSqlPassword: boolean; trustServerCertificate: boolean; sqlServiceIdentity: string; backupDirectory: string; installerAvailable: boolean; agentPackageAvailable: boolean };
export function InstallationSettings({ api }: { api: Api }) {
  const [settings, setSettings] = useState<Settings | null>(null);
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  async function load() {
    setError(''); setBusy(true);
    try { const s = await api('/installation/settings'); setSettings({ ...s, sqlServer: s.sqlServer || 'localhost', sqlUsername: s.sqlUsername || 'sa', trustServerCertificate: s.trustServerCertificate === true }); }
    catch (e) { setError(e instanceof Error ? e.message : 'Não foi possível carregar a configuração de instalação.'); }
    finally { setBusy(false); }
  }
  useEffect(() => { void load(); }, []);
  async function save(e: FormEvent) {
    e.preventDefault(); if (!settings) return;
    setError(''); setNotice('');
    let origin: string;
    try {
      origin = centralOrigin(settings.centralUrl);
    } catch { setError('Informe apenas a origem HTTPS real da central, como https://backup.suaempresa.com, sem caminho ou credenciais.'); return; }
    setBusy(true);
    try {
      await api('/installation/settings', 'PUT', { centralUrl: origin, sqlServer: settings.sqlServer.trim(), sqlUsername: settings.sqlUsername.trim(), ...(password ? { sqlPassword: password } : {}), trustServerCertificate: settings.trustServerCertificate, sqlServiceIdentity: settings.sqlServiceIdentity.trim(), backupDirectory: settings.backupDirectory.trim() });
      setPassword('');
      const saved = await api('/installation/settings');
      setSettings({ ...saved, trustServerCertificate: saved.trustServerCertificate === true });
      setNotice('Padrões salvos. Agentes 0.5 ou superiores recebem a conexão SQL pela API entre operações, testam e confirmam a aplicação. Agentes antigos precisam de uma atualização inicial.');
    } catch (e) { setError(e instanceof Error ? e.message : 'Não foi possível salvar os padrões.'); }
    finally { setBusy(false); }
  }
  if (!settings) return <section className="panel installation-message" aria-live="polite">{error ? <><div className="alert error" role="alert">{error}</div><button className="secondary" onClick={load} disabled={busy}>Tentar novamente</button></> : <p>Carregando padrões de instalação…</p>}</section>;
  const update = (key: keyof Settings, value: string | boolean) => setSettings({ ...settings, [key]: value });
  return <div className="settings-grid"><form className="panel settings-form" onSubmit={save}>
    <div className="panel-heading"><div><h2>Central e conexão SQL compartilhada</h2><p>Configure uma vez. Agentes 0.5 ou superiores recebem as mudanças SQL automaticamente, sem novo executável.</p></div></div>
    <fieldset disabled={busy} className="installation-fields">
    <label>Endereço HTTPS da central<input type="url" required placeholder="https://backup.suaempresa.com" value={settings.centralUrl} onChange={e => update('centralUrl', e.target.value)} /><small>Use o endereço real acessível nas máquinas dos clientes. O endereço deste navegador não será preenchido automaticamente.</small></label>
    <div className="form-grid"><label>Servidor SQL<input required value={settings.sqlServer} onChange={e => update('sqlServer', e.target.value)} placeholder="localhost" /></label><label>Usuário SQL<input required value={settings.sqlUsername} onChange={e => update('sqlUsername', e.target.value)} placeholder="sa" autoComplete="off" /></label></div>
    <label>Senha SQL<input type="password" autoComplete="new-password" value={password} required={!settings.hasSqlPassword} placeholder={settings.hasSqlPassword ? 'Senha salva — deixe vazio para manter' : 'Informe a senha SQL compartilhada'} onChange={e => setPassword(e.target.value)} /><small>{settings.hasSqlPassword ? 'Senha salva com proteção na central. Seu valor nunca é devolvido ao painel.' : 'Salve a senha compartilhada antes de aprovar a instalação de uma máquina.'} Deixar o campo vazio preserva a senha salva.</small></label>
    <label className="checkbox"><input type="checkbox" checked={settings.trustServerCertificate} onChange={e => update('trustServerCertificate', e.target.checked)} />Confiar no certificado do servidor SQL</label>
    <label>Identidade do serviço SQL (opcional)<input value={settings.sqlServiceIdentity} onChange={e => update('sqlServiceIdentity', e.target.value)} placeholder="Detectar automaticamente no Windows" /><small>Vazio: o instalador detecta a conta do serviço SQL local.</small></label>
    <label>Pasta local de backup (opcional)<input value={settings.backupDirectory} onChange={e => update('backupDirectory', e.target.value)} placeholder="Usar pasta padrão do agente" /><small>Vazio: usa a pasta padrão. Banco, pasta FTP e agenda continuam no cadastro de cada cliente.</small></label>
    </fieldset>
    {error && <div className="alert error" role="alert">{error}</div>}{notice && <div className="alert success" role="status">{notice}</div>}
    <div className="form-footer"><button className="primary" disabled={busy}>{busy ? 'Salvando…' : 'Salvar configuração compartilhada'}</button></div>
  </form><section className="panel installation-message"><h2>Instalador Windows único</h2><p>Baixe o mesmo instalador genérico para todas as máquinas. No Windows, informe o nome do cliente e solicite o cadastro. Em Clientes, confira o código temporário e aprove a instalação. O código pendente expira em 15 minutos.</p><a className="primary universal-installer" href="/api/installer/download" download>Baixar instalador genérico</a><p>Pacote do instalador: <strong>{settings.installerAvailable ? 'Disponível' : 'Indisponível'}</strong><br />Pacote do agente: <strong>{settings.agentPackageAvailable ? 'Disponível' : 'Indisponível'}</strong></p>{(!settings.installerAvailable || !settings.agentPackageAvailable) && <div className="alert error">A distribuição ainda está incompleta. Solicite à operação a publicação do instalador e do pacote do agente antes de baixar o instalador.</div>}<p>Agentes 0.4 precisam de uma atualização inicial para suportar alterações SQL pela API. Depois, mudanças de usuário, senha e instância são testadas e salvas pelo agente, sem reinstalar. Se o teste falhar, a conexão anterior é preservada. A pasta local e a conta do serviço não são alteradas remotamente.</p></section></div>;
}
