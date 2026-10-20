// Genera azuredeploy.json (Logic App de consumo) y email-preview.html desde la MISMA plantilla de
// correo, para que la vista previa no se desincronice de lo que envía la Logic App.
// Uso: node scripts/generate-logicapp.js Sluice.Portal/Deploy/LogicApp
// (regenerar tras cambiar el correo; los dos archivos generados se versionan).
const fs = require('fs');
const path = require('path');
const out = process.argv[2];

// ---------- Plantilla de correo (tablas + estilos en línea: Outlook no soporta CSS moderno) ----------
const FONT = "font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;";
const DETAIL_START = (label) =>
  `<tr><td style="padding:9px 0;border-bottom:1px solid #e3e9ef;font-size:13px;color:#4a5d72;width:170px;vertical-align:top;">${label}</td>` +
  `<td style="padding:9px 0;border-bottom:1px solid #e3e9ef;font-size:13px;color:#0b1f33;font-weight:600;word-break:break-all;">`;
const DETAIL_END = '</td></tr>';
const TABLES_START =
  '<tr><td style="padding:14px 28px 4px;">' +
  '<p style="margin:0 0 8px;font-size:11px;font-weight:700;letter-spacing:0.8px;color:#4a5d72;">TABLAS AFECTADAS</p>' +
  '<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #e3e9ef;border-radius:8px;border-collapse:separate;">';
const TABLES_END = '</table></td></tr>';
const TABLE_ROW_START =
  "<tr><td style=\"padding:8px 12px;border-bottom:1px solid #eef2f6;font-family:Consolas,'Courier New',monospace;font-size:13px;color:#0b1f33;\">";
const TABLE_ROW_END = '</td></tr>';
const BUTTON_START = '<tr><td style="padding:22px 28px 4px;"><a href="';
const BUTTON_END =
  '" style="display:inline-block;padding:11px 22px;border-radius:6px;background:#0f6cbd;color:#ffffff;font-size:14px;font-weight:600;text-decoration:none;">Abrir en Sluice</a></td></tr>';
const LOGO_START = '<td style="padding-right:10px;vertical-align:middle;"><img src="';
const LOGO_END = '" width="28" height="28" alt="" style="display:block;border:0;border-radius:6px;"></td>';
// Logo dibujado con tablas y estilos en línea (sin imagen): se ve en cualquier cliente aunque el portal
// no sea accesible desde Internet. Imita logo.svg: cuadro, barra azul (la compuerta) y ondas (el agua).
const LOGO_HTML =
  '<td style="padding-right:10px;vertical-align:middle;">' +
  '<table role="presentation" cellpadding="0" cellspacing="0" width="30" style="width:30px;background:#13304d;border:1px solid #2a4a6b;border-radius:7px;">' +
  '<tr><td align="center" style="padding:7px 0 1px;font-size:0;line-height:0;">' +
  '<table role="presentation" cellpadding="0" cellspacing="0" width="16"><tr><td style="width:16px;height:3px;background:#4ca3f0;border-radius:2px;font-size:0;line-height:3px;">&nbsp;</td></tr></table>' +
  '</td></tr>' +
  "<tr><td align=\"center\" style=\"padding:0 0 4px;color:#ffffff;font-family:'Segoe UI',Arial,sans-serif;font-size:20px;line-height:16px;font-weight:700;\">&#8776;</td></tr>" +
  '</table></td>';

const EMAIL = `<!DOCTYPE html>
<html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sluice</title></head>
<body style="margin:0;padding:0;background:#eef2f6;">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#eef2f6;padding:32px 12px;${FONT}">
<tr><td align="center">
<table role="presentation" width="600" cellpadding="0" cellspacing="0" style="width:100%;max-width:600px;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 2px 10px rgba(11,31,51,0.10);${FONT}">
<tr><td style="background:#0b1f33;padding:18px 28px;">
<table role="presentation" cellpadding="0" cellspacing="0"><tr>{{LOGO}}<td style="color:#ffffff;font-size:17px;font-weight:600;letter-spacing:0.2px;vertical-align:middle;">Sluice Portal</td></tr></table>
</td></tr>
<tr><td style="height:4px;line-height:4px;font-size:0;background:{{COLOR}};">&nbsp;</td></tr>
<tr><td style="padding:28px 28px 6px;">
<span style="display:inline-block;padding:3px 10px;border-radius:999px;background:{{COLOR}};color:#ffffff;font-size:11px;font-weight:700;letter-spacing:0.8px;">{{ETIQUETA}}</span>
<h1 style="margin:14px 0 8px;font-size:22px;line-height:1.3;color:#0b1f33;font-weight:600;">{{TITULO}}</h1>
<p style="margin:0;font-size:14px;line-height:1.6;color:#4a5d72;">{{MENSAJE}}</p>
</td></tr>
<tr><td style="padding:14px 28px 4px;">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-top:1px solid #e3e9ef;">{{DETALLES}}</table>
</td></tr>
{{TABLAS}}
{{BOTON}}
<tr><td style="padding:22px 28px 26px;">
<p style="margin:0;font-size:12px;line-height:1.6;color:#7d93ab;">Enviado por Sluice Portal · {{FECHA}}<br>Recibes este correo porque tu dirección está en la lista de avisos de la Logic App de alertas de Sluice.</p>
</td></tr>
</table>
</td></tr></table>
</body></html>`;

// Textos por tipo de evento (mismo orden en la Logic App y en la vista previa). Gana la primera entrada
// que coincide: con status, solo cuando el payload trae ese status (un pipeline.failure en Partial llega
// con status "warning"; sin status, o con "error", es un fallo).
const EVENTS = [
  { type: 'pipeline.failure', status: 'warning', label: 'AVISO', title: 'Un pipeline terminó con fallos parciales', color: '#ca5010', importance: 'Normal' },
  { type: 'pipeline.failure', label: 'FALLO', title: 'Un pipeline ha fallado', color: '#c4314b', importance: 'High' },
  { type: 'snapshot.incomplete', label: 'SNAPSHOT INCOMPLETO', title: 'Un snapshot de Synapse Link está incompleto', color: '#038387', importance: 'Normal' },
  { type: 'reconcile.divergence', label: 'DIVERGENCIA', title: 'Divergencia en la conciliación', color: '#c27c0e', importance: 'Normal' },
  { type: 'reconcile.values', label: 'VALORES', title: 'Diferencias de valores entre Parquet y SQL', color: '#7a4fd1', importance: 'Normal' },
  { type: 'test.ping', label: 'PRUEBA', title: 'La Logic App recibe las alertas de Sluice', color: '#0f6cbd', importance: 'Normal' },
];
const FALLBACK = { label: 'ALERTA', title: 'Alerta del sistema', color: '#4a5d72', importance: 'Normal' };
const eventFor = (sample) =>
  EVENTS.find((e) => e.type === sample.eventType && (!e.status || e.status === sample.status)) ?? FALLBACK;
const AUTOFIX = {
  scheduled: 'Programada: se re-sincronizan las tablas una vez',
  off: 'Desactivada: corrige desde Conciliación',
  unresolved: 'No resolvió la diferencia: revisa Conciliación',
};

// ---------- Expresiones de Logic Apps ----------
const lit = (s) => `'${s.replace(/'/g, "''")}'`;
const T = (k) => `triggerBody()?['${k}']`;
const E = `coalesce(${T('eventType')},'')`;
const esc = (x) => `replace(replace(coalesce(${x},''),'&','&amp;'),'<','&lt;')`;
const S = `coalesce(${T('status')},'')`;
const matches = (ev) =>
  ev.status ? `and(equals(${E},${lit(ev.type)}),equals(${S},${lit(ev.status)}))` : `equals(${E},${lit(ev.type)})`;
const byEvent = (key) =>
  EVENTS.reduceRight((acc, ev) => `if(${matches(ev)},${lit(ev[key])},${acc})`, lit(FALLBACK[key]));
const P = `trim(coalesce(parameters('urlPortal'),''))`;
const portal = `if(endsWith(${P},'/'),substring(${P},0,sub(length(${P}),1)),${P})`;
const pipelineName = `coalesce(${T('pipelineName')},${T('pipelineId')})`;
const row = (label, value) => `if(empty(${value}),'',concat(${lit(DETAIL_START(label))},${value},${lit(DETAIL_END)}))`;
const fecha = `concat(formatDateTime(coalesce(${T('timestamp')},utcNow()),'dd/MM/yyyy HH:mm'),' UTC')`;
const AR = `coalesce(${T('autoRemediation')},'')`;
const autofix = `if(equals(${AR},'scheduled'),${lit(AUTOFIX.scheduled)},if(equals(${AR},'off'),${lit(AUTOFIX.off)},if(equals(${AR},'unresolved'),${lit(AUTOFIX.unresolved)},'')))`;
const divergence =
  `if(equals(${T('maxDivergencePct')},null),'',concat(${lit(DETAIL_START('Divergencia máxima'))},formatNumber(float(${T('maxDivergencePct')}),'0.##','es-ES'),' %',${lit(DETAIL_END)}))`;
const outP = (k) => `outputs('Presentacion')?['${k}']`;

const detalles = `concat(${[
  row('Pipeline', esc(pipelineName)),
  row('Snapshot', esc(T('snapshot'))),
  row('Job', esc(T('jobId'))),
  row('Tipo de job', esc(T('jobType'))),
  row('Etapa', esc(T('stage'))),
  divergence,
  row('Corrección automática', outP('autofix')),
  row('Fecha', fecha),
].join(',')})`;

const htmlExpr = EMAIL
  .replace('{{LOGO}}', `@{if(empty(${outP('portal')}),${lit(LOGO_HTML)},concat(${lit(LOGO_START)},${outP('portal')},'/favicon.png',${lit(LOGO_END)}))}`)
  .replaceAll('{{COLOR}}', `@{${outP('color')}}`)
  .replace('{{ETIQUETA}}', `@{${outP('etiqueta')}}`)
  .replace('{{TITULO}}', `@{${outP('titulo')}}`)
  .replace('{{MENSAJE}}', `@{${esc(`coalesce(${T('message')},${T('text')})`)}}`)
  .replace('{{DETALLES}}', `@{${detalles}}`)
  .replace('{{TABLAS}}', `@{if(empty(body('Filas_de_tablas')),'',concat(${lit(TABLES_START)},join(body('Filas_de_tablas'),''),${lit(TABLES_END)}))}`)
  .replace('{{BOTON}}', `@{if(empty(${outP('enlace')}),'',concat(${lit(BUTTON_START)},${outP('enlace')},${lit(BUTTON_END)}))}`)
  .replace('{{FECHA}}', `@{${fecha}}`);

const enlace =
  `if(empty(${portal}),'',concat(${portal},if(startsWith(${E},'reconcile.'),'/reconcile',if(empty(${T('jobId')}),'',concat('/jobs/',${T('jobId')})))))`;

const definition = {
  $schema: 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#',
  contentVersion: '1.0.0.0',
  parameters: {
    $connections: { defaultValue: {}, type: 'Object' },
    destinatarios: { defaultValue: '', type: 'String' },
    urlPortal: { defaultValue: '', type: 'String' },
  },
  triggers: {
    manual: {
      type: 'Request',
      kind: 'Http',
      inputs: {
        schema: {
          type: 'object',
          properties: {
            eventType: { type: 'string' },
            timestamp: { type: 'string' },
            pipelineId: { type: 'string' },
            pipelineName: { type: 'string' },
            jobId: { type: 'string' },
            jobType: { type: 'string' },
            stage: { type: 'string' },
            snapshot: { type: 'string' },
            status: { type: 'string' },
            message: { type: 'string' },
            text: { type: 'string' },
            maxDivergencePct: { type: 'number' },
            entities: { type: 'array', items: { type: 'string' } },
            autoRemediation: { type: 'string' },
          },
        },
      },
    },
  },
  actions: {
    Presentacion: {
      type: 'Compose',
      runAfter: {},
      inputs: {
        etiqueta: `@{${byEvent('label')}}`,
        titulo: `@{${byEvent('title')}}`,
        color: `@{${byEvent('color')}}`,
        importancia: `@{${byEvent('importance')}}`,
        asunto: `@{concat('[Sluice] ',${byEvent('title')},if(empty(${pipelineName}),'',concat(' · ',${pipelineName})))}`,
        autofix: `@{${autofix}}`,
        portal: `@{${portal}}`,
        enlace: `@{${enlace}}`,
      },
    },
    Filas_de_tablas: {
      type: 'Select',
      runAfter: {},
      inputs: {
        from: `@coalesce(${T('entities')},json('[]'))`,
        select: `@concat(${lit(TABLE_ROW_START)},${esc('item()')},${lit(TABLE_ROW_END)})`,
      },
    },
    Html: {
      type: 'Compose',
      runAfter: { Presentacion: ['Succeeded'], Filas_de_tablas: ['Succeeded'] },
      inputs: htmlExpr,
    },
    Enviar_correo: {
      type: 'ApiConnection',
      runAfter: { Html: ['Succeeded'] },
      inputs: {
        host: { connection: { name: "@parameters('$connections')['office365']['connectionId']" } },
        method: 'post',
        path: '/v2/Mail',
        body: {
          To: "@parameters('destinatarios')",
          Subject: `@{${outP('asunto')}}`,
          Body: "@{outputs('Html')}",
          Importance: `@{${outP('importancia')}}`,
        },
      },
    },
  },
  outputs: {},
};

const managedApi = "[concat(subscription().id, '/providers/Microsoft.Web/locations/', parameters('location'), '/managedApis/office365')]";
const template = {
  $schema: 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#',
  contentVersion: '1.0.0.0',
  metadata: {
    description: 'Sluice Portal: Logic App (Consumption) que recibe el webhook de alertas del portal y las envía por correo (Office 365 Outlook) con plantilla HTML.',
  },
  parameters: {
    logicAppName: {
      type: 'string', defaultValue: 'sluice-alertas', minLength: 1, maxLength: 80,
      metadata: { description: 'Nombre de la Logic App.' },
    },
    location: {
      type: 'string', defaultValue: '[resourceGroup().location]',
      metadata: { description: 'Región de la Logic App y de la conexión.' },
    },
    recipients: {
      type: 'string', minLength: 3,
      metadata: { description: 'Destinatarios del correo, separados por punto y coma (p. ej. operaciones@contoso.com;dba@contoso.com).' },
    },
    portalBaseUrl: {
      type: 'string', defaultValue: '',
      metadata: { description: 'URL pública del portal Sluice (p. ej. https://sluice.contoso.com). Opcional: con ella el correo usa el logo del portal (sin ella, uno dibujado en HTML) y trae el botón "Abrir en Sluice".' },
    },
    office365ConnectionName: {
      type: 'string', defaultValue: 'sluice-office365',
      metadata: { description: 'Nombre del recurso de conexión de Office 365 Outlook.' },
    },
  },
  resources: [
    {
      type: 'Microsoft.Web/connections',
      apiVersion: '2016-06-01',
      name: "[parameters('office365ConnectionName')]",
      location: "[parameters('location')]",
      properties: {
        displayName: 'Sluice - Office 365 Outlook',
        api: { id: managedApi },
      },
    },
    {
      type: 'Microsoft.Logic/workflows',
      apiVersion: '2019-05-01',
      name: "[parameters('logicAppName')]",
      location: "[parameters('location')]",
      tags: { aplicacion: 'sluice-portal', proposito: 'alertas' },
      dependsOn: ["[resourceId('Microsoft.Web/connections', parameters('office365ConnectionName'))]"],
      properties: {
        state: 'Enabled',
        definition,
        parameters: {
          $connections: {
            value: {
              office365: {
                id: managedApi,
                connectionId: "[resourceId('Microsoft.Web/connections', parameters('office365ConnectionName'))]",
                connectionName: "[parameters('office365ConnectionName')]",
              },
            },
          },
          destinatarios: { value: "[parameters('recipients')]" },
          urlPortal: { value: "[parameters('portalBaseUrl')]" },
        },
      },
    },
  ],
  outputs: {
    logicAppName: { type: 'string', value: "[parameters('logicAppName')]" },
    connectionName: { type: 'string', value: "[parameters('office365ConnectionName')]" },
    triggerUrl: {
      type: 'string',
      value: "[listCallbackUrl(resourceId('Microsoft.Logic/workflows/triggers', parameters('logicAppName'), 'manual'), '2019-05-01').value]",
    },
  },
};

fs.writeFileSync(path.join(out, 'azuredeploy.json'), JSON.stringify(template, null, 2) + '\n');

// ---------- Vista previa: la misma plantilla con datos de ejemplo ----------
const escHtml = (s) => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;');
const PORTAL = 'https://sluice.contoso.com';

function render(sample) {
  const ev = eventFor(sample);
  const pipeline = sample.pipelineName ?? sample.pipelineId;
  const detail = (label, value) => (value ? DETAIL_START(label) + value + DETAIL_END : '');
  const details = [
    detail('Pipeline', escHtml(pipeline)),
    detail('Snapshot', escHtml(sample.snapshot)),
    detail('Job', escHtml(sample.jobId)),
    detail('Tipo de job', escHtml(sample.jobType)),
    detail('Etapa', escHtml(sample.stage)),
    sample.maxDivergencePct == null ? '' : detail('Divergencia máxima', `${sample.maxDivergencePct.toLocaleString('es-ES')} %`),
    detail('Corrección automática', AUTOFIX[sample.autoRemediation] ?? ''),
    detail('Fecha', '09/10/2026 22:41 UTC'),
  ].join('');
  const tables = sample.entities?.length
    ? TABLES_START + sample.entities.map((t) => TABLE_ROW_START + escHtml(t) + TABLE_ROW_END).join('') + TABLES_END
    : '';
  const link = sample.eventType.startsWith('reconcile.') ? `${PORTAL}/reconcile` : sample.jobId ? `${PORTAL}/jobs/${sample.jobId}` : PORTAL;
  return EMAIL
    .replace('{{LOGO}}', LOGO_HTML)
    .replaceAll('{{COLOR}}', ev.color)
    .replace('{{ETIQUETA}}', ev.label)
    .replace('{{TITULO}}', ev.title)
    .replace('{{MENSAJE}}', escHtml(sample.message))
    .replace('{{DETALLES}}', details)
    .replace('{{TABLAS}}', tables)
    .replace('{{BOTON}}', BUTTON_START + link + BUTTON_END)
    .replace('{{FECHA}}', '09/10/2026 22:41 UTC');
}

const SAMPLES = [
  { eventType: 'reconcile.divergence', pipelineName: 'Lean', jobId: 'job-lean-20261009224112-a1b2c3', maxDivergencePct: 4.2, autoRemediation: 'scheduled',
    entities: ['custtable', 'vendtable', 'salesline'], message: 'La divergencia de conteos superó el umbral en el pipeline Lean.' },
  { eventType: 'reconcile.values', pipelineName: 'Lean', jobId: 'job-lean-20261009230500-d4e5f6', autoRemediation: 'off',
    entities: ['purchreqbusinessjustificationcodes'], message: 'Hay filas con valores distintos entre el Parquet consolidado y SQL.' },
  { eventType: 'pipeline.failure', pipelineName: 'Lean', jobId: 'job-lean-20261009231000-778899', jobType: 'Incremental', stage: 'sql',
    message: 'Login failed for user. The login is from an untrusted domain.' },
  { eventType: 'pipeline.failure', status: 'warning', pipelineName: 'Lean', jobId: 'job-lean-20261009233000-aabbcc', jobType: 'Incremental', stage: 'ingest',
    message: 'Ingest completed: 3 snapshot(s), 48 entity(ies). 2 entity(ies) failed: inventtrans, salesline.' },
  { eventType: 'snapshot.incomplete', status: 'warning', pipelineName: 'Lean', snapshot: '2026-10-09T22.00.31Z',
    message: "Pipeline Lean parado: el snapshot 2026-10-09T22.00.31Z está cerrado pero incompleto desde hace 45 min (Snapshot '2026-10-09T22.00.31Z' has no non-empty CSV in any entity (empty or in-progress export).). No se procesa nada más hasta que el volcado de Synapse Link se complete; revisa la exportación." },
  { eventType: 'test.ping', message: 'Sluice portal alert test' },
];

// Cada correo va en un iframe (srcdoc) para verlo tal cual, sin mezclar estilos con la página.
const frames = SAMPLES.map((s) => {
  const ev = eventFor(s);
  const doc = render(s).replace(/&/g, '&amp;').replace(/"/g, '&quot;');
  const caption = ev.status ? `${s.eventType} · ${ev.status}` : s.eventType;
  return `<section><h2><span style="background:${ev.color}"></span>${ev.title}<code>${caption}</code></h2><iframe title="${ev.title}" srcdoc="${doc}"></iframe></section>`;
}).join('\n');

const preview = `<!DOCTYPE html>
<html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Sluice · Vista previa de los correos de alerta</title>
<style>
  :root { color-scheme: light; }
  body { margin: 0; background: #0b1f33; color: #e8eef5; font-family: 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; }
  header { padding: 40px 24px 8px; max-width: 1100px; margin: 0 auto; }
  header h1 { margin: 0 0 8px; font-size: 28px; font-weight: 600; color: #fff; }
  header p { margin: 0; color: #a9b8c9; line-height: 1.6; max-width: 720px; }
  main { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 520px), 1fr)); gap: 24px; padding: 24px; max-width: 1100px; margin: 0 auto; }
  section { background: #13304d; border-radius: 12px; padding: 16px; }
  h2 { display: flex; align-items: center; gap: 10px; margin: 0 0 12px; font-size: 15px; font-weight: 600; flex-wrap: wrap; }
  h2 span { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
  h2 code { margin-left: auto; font-size: 12px; color: #7d93ab; }
  iframe { width: 100%; height: 760px; border: 0; border-radius: 8px; background: #eef2f6; }
</style></head>
<body>
<header>
  <h1>Correos de alerta de Sluice</h1>
  <p>Así se ven los correos que envía la Logic App del paquete para cada tipo de evento del portal. Los datos son de ejemplo; el diseño es el de <code>azuredeploy.json</code> (ambos salen de la misma plantilla).</p>
</header>
<main>
${frames}
</main>
</body></html>
`;
fs.writeFileSync(path.join(out, 'email-preview.html'), preview);
console.log('ok', Buffer.byteLength(JSON.stringify(template)), 'bytes');
