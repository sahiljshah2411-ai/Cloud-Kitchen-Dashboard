using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace RevitCadQC.Core.Report
{
    /// <summary>
    /// Single self-contained HTML file (no internet needed): summary, per-floor plan viewer with the CAD
    /// linework, the Revit model and every issue clouded on top, and a filterable issue register.
    /// </summary>
    public static class HtmlReportWriter
    {
        public static void Write(QcReport r, string path)
        {
            var data = new
            {
                project = r.ProjectName,
                revitFile = r.RevitFile,
                cadFolder = r.CadFolder,
                run = r.RunUtc.ToLocalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture),
                duration = r.DurationSeconds,
                version = r.ToolVersion,
                log = r.Log.Take(400),
                floors = r.Floors.Select(f => new
                {
                    floor = f.Floor,
                    level = f.LevelName,
                    code = f.FloorCode,
                    cad = Path.GetFileName(f.CadFile ?? ""),
                    title = f.RegionTitle,
                    units = f.UnitsNote,
                    align = f.Alignment == null ? null : new
                    {
                        method = f.Alignment.Method,
                        match = Math.Round(f.Alignment.InlierRatio * 100, 1),
                        rms = f.Alignment.RmsMm < 1e9 ? Math.Round(f.Alignment.RmsMm, 1) : (double?)null,
                        ok = f.Alignment.Reliable
                    },
                    cadCounts = f.CadCounts,
                    rvtCounts = f.RevitCounts,
                    notes = f.Notes,
                    geo = f.Overlay == null ? null : new
                    {
                        cad = Flat(f.Overlay.CadLines.SelectMany(s => new[] { s.A.X, s.A.Y, s.B.X, s.B.Y })),
                        walls = f.Overlay.RevitWalls.Select(w => Flat(w.SelectMany(p => new[] { p.X, p.Y }))),
                        cols = f.Overlay.RevitColumns.Select(w => Flat(w.SelectMany(p => new[] { p.X, p.Y }))),
                        grids = f.Overlay.RevitGrids.Select(s => Flat(new[] { s.A.X, s.A.Y, s.B.X, s.B.Y })),
                        box = f.Overlay.Bounds.IsEmpty ? null : Flat(new[] { f.Overlay.Bounds.MinX, f.Overlay.Bounds.MinY, f.Overlay.Bounds.MaxX, f.Overlay.Bounds.MaxY })
                    }
                }),
                issues = r.Issues.Select(i => new
                {
                    id = i.Id,
                    st = i.Status.ToString(),
                    sev = i.Severity.ToString(),
                    floor = i.Floor,
                    level = i.LevelName,
                    cat = i.Category,
                    type = i.IssueType,
                    title = i.Title,
                    desc = i.Description,
                    cadV = i.CadValue,
                    rvtV = i.RevitValue,
                    delta = i.Delta,
                    x = i.RevitLocation.HasValue ? Math.Round(i.RevitLocation.Value.X) : (double?)null,
                    y = i.RevitLocation.HasValue ? Math.Round(i.RevitLocation.Value.Y) : (double?)null,
                    r = Math.Round(i.MarkerRadius),
                    path = i.RevitPath == null ? null : Flat(i.RevitPath.SelectMany(p => new[] { p.X, p.Y })),
                    ids = i.RevitElementIds,
                    layer = i.CadLayer,
                    cadFile = Path.GetFileName(i.CadFile ?? "")
                })
            };
            string json = JsonConvert.SerializeObject(data, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })
                .Replace("</", "<\\/");
            var html = Template.Replace("/*__DATA__*/null", json)
                               .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(r.ProjectName ?? "Revit CAD QC"));
            File.WriteAllText(path, html, new UTF8Encoding(false));
        }

        private static IEnumerable<long> Flat(IEnumerable<double> v) => v.Select(x => (long)Math.Round(x));

        private const string Template = @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>__TITLE__ · CAD vs Revit QC</title>
<style>
:root{--bg:#f5f6f8;--panel:#fff;--ink:#1c2230;--muted:#667085;--line:#e3e6eb;--accent:#1f3a5f;--cad:#8a93a3;--rvt:#2f6fde;
--crit:#d92d20;--major:#f58a07;--minor:#d4a500;--info:#1d8cb8;--ok:#12a150;--grid:#b39ddb}
@media (prefers-color-scheme:dark){:root{--bg:#10131a;--panel:#171b24;--ink:#e6e9ef;--muted:#98a2b3;--line:#2a3140;--accent:#9dbcf0;--cad:#6b7385;--rvt:#5b9bff}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.45 system-ui,-apple-system,Segoe UI,Roboto,sans-serif}
header{padding:20px 24px 12px;border-bottom:1px solid var(--line);background:var(--panel)}
h1{margin:0 0 4px;font-size:20px}h2{font-size:15px;margin:0 0 10px}.muted{color:var(--muted)}
main{padding:16px 24px;max-width:1700px;margin:0 auto}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:16px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
.card b{display:block;font-size:26px;line-height:1.1}.card span{color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.04em}
.sev-Critical{color:var(--crit)}.sev-Major{color:var(--major)}.sev-Minor{color:var(--minor)}.sev-Info{color:var(--info)}
.grid2{display:grid;grid-template-columns:minmax(0,1.35fr) minmax(0,1fr);gap:16px}
@media (max-width:1100px){.grid2{grid-template-columns:1fr}}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px}
.tabs{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:10px}
.tab{border:1px solid var(--line);background:transparent;color:var(--ink);border-radius:999px;padding:4px 12px;cursor:pointer;font-size:13px}
.tab.on{background:var(--accent);color:#fff;border-color:var(--accent)}
.viewer{position:relative;height:620px;border:1px solid var(--line);border-radius:8px;overflow:hidden;background:var(--bg);touch-action:none}
.viewer svg{width:100%;height:100%;cursor:grab;display:block}
.legend{display:flex;flex-wrap:wrap;gap:12px;font-size:12px;margin:8px 0}.legend label{display:flex;gap:5px;align-items:center;cursor:pointer}
.sw{width:14px;height:4px;border-radius:2px;display:inline-block}
.meta{font-size:12px;color:var(--muted);margin-top:8px}
.counts{width:100%;border-collapse:collapse;font-size:12px;margin-top:8px}.counts td,.counts th{border-bottom:1px solid var(--line);padding:3px 6px;text-align:right}.counts th:first-child,.counts td:first-child{text-align:left}
.filters{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:10px}
select,input{background:var(--panel);color:var(--ink);border:1px solid var(--line);border-radius:6px;padding:5px 8px;font:inherit;font-size:13px}
.list{max-height:700px;overflow:auto}
table.issues{width:100%;border-collapse:collapse;font-size:13px}
table.issues th{position:sticky;top:0;background:var(--panel);text-align:left;font-size:12px;color:var(--muted);border-bottom:1px solid var(--line);padding:6px;cursor:pointer}
table.issues td{border-bottom:1px solid var(--line);padding:6px;vertical-align:top}
table.issues tr{cursor:pointer}table.issues tr:hover td{background:rgba(127,127,127,.08)}table.issues tr.sel td{background:rgba(47,111,222,.14)}
.pill{display:inline-block;border-radius:999px;padding:1px 8px;font-size:11px;font-weight:600;color:#fff}
.pill.Critical{background:var(--crit)}.pill.Major{background:var(--major)}.pill.Minor{background:var(--minor);color:#222}.pill.Info{background:var(--info)}
.st{font-size:11px;color:var(--muted)}.st.New{color:var(--crit);font-weight:600}.st.Resolved{color:var(--ok)}
.detail{font-size:13px;margin-top:10px;padding:10px;border:1px dashed var(--line);border-radius:8px;min-height:60px}
.detail dt{color:var(--muted);font-size:11px;text-transform:uppercase}.detail dd{margin:0 0 6px}
details{margin-top:16px}pre{white-space:pre-wrap;font-size:12px;color:var(--muted)}
.bar{height:8px;border-radius:4px;background:var(--line);overflow:hidden;display:flex}.bar i{display:block;height:100%}
@media print{.viewer{height:420px}.list{max-height:none}}
</style></head><body>
<header><h1 id=""h""></h1><div class=""muted"" id=""sub""></div></header>
<main>
<div class=""cards"" id=""cards""></div>
<div class=""grid2"">
 <section class=""panel"">
  <h2>Plan overlay</h2>
  <div class=""tabs"" id=""tabs""></div>
  <div class=""legend"">
   <label><input type=""checkbox"" id=""lCad"" checked><span class=""sw"" style=""background:var(--cad)""></span>CAD linework</label>
   <label><input type=""checkbox"" id=""lRvt"" checked><span class=""sw"" style=""background:var(--rvt)""></span>Revit walls &amp; columns</label>
   <label><input type=""checkbox"" id=""lGrid""><span class=""sw"" style=""background:var(--grid)""></span>Revit grids</label>
   <label><input type=""checkbox"" id=""lIss"" checked><span class=""sw"" style=""background:var(--crit)""></span>Issues</label>
   <button class=""tab"" id=""fit"">Fit</button>
  </div>
  <div class=""viewer"" id=""viewer""><svg id=""svg"" xmlns=""http://www.w3.org/2000/svg""><g id=""world""></g></svg></div>
  <div class=""meta"" id=""fmeta""></div>
  <table class=""counts"" id=""fcounts""></table>
 </section>
 <section class=""panel"">
  <h2>Issue register</h2>
  <div class=""filters"">
   <select id=""fFloor""></select><select id=""fSev""></select><select id=""fCat""></select><select id=""fSt""></select>
   <input id=""fTxt"" placeholder=""Search…"" style=""flex:1;min-width:120px"">
  </div>
  <div class=""muted"" id=""shown"" style=""font-size:12px;margin-bottom:6px""></div>
  <div class=""list""><table class=""issues""><thead><tr><th data-k=""id"">ID</th><th data-k=""sev"">Severity</th><th data-k=""cat"">Category</th><th data-k=""title"">Issue</th><th data-k=""st"">Status</th></tr></thead><tbody id=""tb""></tbody></table></div>
  <div class=""detail"" id=""detail""><span class=""muted"">Select an issue to see details.</span></div>
 </section>
</div>
<details><summary>Run log</summary><pre id=""log""></pre></details>
</main>
<script>
const D=/*__DATA__*/null;
const $=id=>document.getElementById(id);
const SEV=['Critical','Major','Minor','Info'];
const esc=s=>(s==null?'':String(s)).replace(/[&<>""]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;'}[c]));
$('h').textContent=(D.project||'Revit model')+' — CAD vs Revit technical QC';
$('sub').textContent=D.run+' · '+(D.revitFile||'')+' · CAD: '+(D.cadFolder||'')+' · '+D.duration+' s · v'+D.version;
$('log').textContent=(D.log||[]).join('\n');
const open=D.issues.filter(i=>i.st!=='Resolved');
function card(v,l,cls){return `<div class=""card""><b class=""${cls||''}"">${v}</b><span>${l}</span></div>`}
let cards=card(open.length,'Open issues');
SEV.forEach(s=>cards+=card(open.filter(i=>i.sev===s).length,s,'sev-'+s));
cards+=card(D.issues.filter(i=>i.st==='New').length,'New since last run');
cards+=card(D.issues.filter(i=>i.st==='Resolved').length,'Resolved','');
cards+=card(D.floors.length,'Floors checked');
$('cards').innerHTML=cards;

// ---------- filters ----------
function opts(el,label,vals){el.innerHTML=`<option value="""">${label}: all</option>`+vals.map(v=>`<option>${esc(v)}</option>`).join('')}
opts($('fFloor'),'Floor',[...new Set(D.issues.map(i=>i.floor).filter(Boolean))]);
opts($('fSev'),'Severity',SEV);
opts($('fCat'),'Category',[...new Set(D.issues.map(i=>i.cat))].sort());
opts($('fSt'),'Status',['New','Open','Accepted','Resolved']);
let sortK='sev',sortDir=1,selId=null,curFloor=0;
function filtered(){
 const f=$('fFloor').value,s=$('fSev').value,c=$('fCat').value,st=$('fSt').value,t=$('fTxt').value.toLowerCase();
 return D.issues.filter(i=>(!f||i.floor===f)&&(!s||i.sev===s)&&(!c||i.cat===c)&&(!st||i.st===st)&&
   (!t||[i.id,i.title,i.desc,i.type,i.cadV,i.rvtV,(i.ids||[]).join(' ')].join(' ').toLowerCase().includes(t)))
 .sort((a,b)=>{let x=a[sortK],y=b[sortK];if(sortK==='sev'){x=SEV.indexOf(x);y=SEV.indexOf(y)}return (x>y?1:x<y?-1:0)*sortDir});
}
function renderList(){
 const rows=filtered();
 $('shown').textContent=rows.length+' of '+D.issues.length+' issues';
 $('tb').innerHTML=rows.map(i=>`<tr data-id=""${esc(i.id)}"" class=""${i.id===selId?'sel':''}""><td>${esc(i.id)}</td><td><span class=""pill ${i.sev}"">${i.sev}</span></td><td>${esc(i.cat)}<div class=""st"">${esc(i.type)}</div></td><td>${esc(i.title)}</td><td class=""st ${i.st}"">${i.st}</td></tr>`).join('');
 drawIssues();
}
['fFloor','fSev','fCat','fSt'].forEach(id=>$(id).onchange=()=>{if(id==='fFloor'&&$('fFloor').value){const k=D.floors.findIndex(f=>f.floor===$('fFloor').value);if(k>=0)showFloor(k)}renderList()});
$('fTxt').oninput=renderList;
document.querySelectorAll('th[data-k]').forEach(th=>th.onclick=()=>{const k=th.dataset.k;sortDir=sortK===k?-sortDir:1;sortK=k;renderList()});
$('tb').onclick=e=>{const tr=e.target.closest('tr');if(tr)select(tr.dataset.id,true)};

function select(id,zoom){
 selId=id;const i=D.issues.find(x=>x.id===id);if(!i)return;
 const k=D.floors.findIndex(f=>f.floor===i.floor);if(k>=0&&k!==curFloor)showFloor(k);
 document.querySelectorAll('#tb tr').forEach(tr=>tr.classList.toggle('sel',tr.dataset.id===id));
 const tr=document.querySelector(`#tb tr[data-id=""${CSS.escape(id)}""]`);if(tr&&!zoom)tr.scrollIntoView({block:'nearest'});
 $('detail').innerHTML=`<dl><dt>${esc(i.id)} · ${esc(i.floor||'')} · ${esc(i.level||'')}</dt><dd><span class=""pill ${i.sev}"">${i.sev}</span> <b>${esc(i.title)}</b></dd>
 <dt>What was found</dt><dd>${esc(i.desc)}</dd>
 ${i.cadV||i.rvtV?`<dt>CAD / Revit</dt><dd>${esc(i.cadV||'—')} &nbsp;→&nbsp; ${esc(i.rvtV||'—')}${i.delta!=null?` &nbsp;(Δ ${i.delta} mm)`:''}</dd>`:''}
 ${i.ids&&i.ids.length?`<dt>Revit element ids</dt><dd>${esc(i.ids.join(', '))}</dd>`:''}
 ${i.layer?`<dt>CAD layer / file</dt><dd>${esc(i.layer)} · ${esc(i.cadFile)}</dd>`:''}
 ${i.x!=null?`<dt>Revit location (mm)</dt><dd>X ${i.x}, Y ${i.y}</dd>`:''}</dl>`;
 if(zoom&&i.x!=null)zoomTo(i.x,i.y,Math.max(4000,i.r*8));
 drawIssues();
}

// ---------- viewer ----------
const svg=$('svg'),world=$('world');let vb=[0,0,1000,1000];
function setVB(){svg.setAttribute('viewBox',vb.join(' '))}
function fit(){const f=D.floors[curFloor];if(!f||!f.geo||!f.geo.box)return;const b=f.geo.box,pad=Math.max(b[2]-b[0],b[3]-b[1])*0.05+500;
 const w=b[2]-b[0]+2*pad,h=b[3]-b[1]+2*pad,r=svg.clientWidth/Math.max(1,svg.clientHeight);let W=w,H=h;if(W/H<r)W=H*r;else H=W/r;
 vb=[(b[0]+b[2])/2-W/2,-(b[1]+b[3])/2-H/2,W,H];setVB();}
function zoomTo(x,y,size){const r=svg.clientWidth/Math.max(1,svg.clientHeight);vb=[x-size*r/2,-y-size/2,size*r,size];setVB();}
$('fit').onclick=fit;
function tabs(){$('tabs').innerHTML=D.floors.map((f,k)=>`<button class=""tab ${k===curFloor?'on':''}"" data-k=""${k}"">${esc(f.floor)} <span class=""muted"">(${D.issues.filter(i=>i.floor===f.floor&&i.st!=='Resolved').length})</span></button>`).join('');
 document.querySelectorAll('#tabs .tab').forEach(b=>b.onclick=()=>{showFloor(+b.dataset.k);renderList()});}
function pathD(a,close){let s='';for(let j=0;j<a.length;j+=2)s+=(j?'L':'M')+a[j]+' '+(-a[j+1]);return s+(close?'Z':'')}
function showFloor(k){
 curFloor=k;tabs();const f=D.floors[k];world.innerHTML='';
 if(!f){return}
 const g=f.geo||{};
 let cad='';const c=g.cad||[];for(let j=0;j<c.length;j+=4)cad+=`M${c[j]} ${-c[j+1]}L${c[j+2]} ${-c[j+3]}`;
 world.insertAdjacentHTML('beforeend',`<g id=""gGrid"" stroke=""var(--grid)"" stroke-dasharray=""400 200"" vector-effect=""non-scaling-stroke"" style=""display:${$('lGrid').checked?'':'none'}"">${(g.grids||[]).map(s=>`<path d=""${pathD(s)}"" vector-effect=""non-scaling-stroke"" stroke-width=""1""/>`).join('')}</g>`);
 world.insertAdjacentHTML('beforeend',`<g id=""gRvt"" style=""display:${$('lRvt').checked?'':'none'}"" fill=""rgba(47,111,222,.22)"" stroke=""var(--rvt)"">${(g.walls||[]).concat(g.cols||[]).map(w=>`<path d=""${pathD(w,1)}"" vector-effect=""non-scaling-stroke"" stroke-width=""1""/>`).join('')}</g>`);
 world.insertAdjacentHTML('beforeend',`<path id=""gCad"" d=""${cad}"" stroke=""var(--cad)"" fill=""none"" vector-effect=""non-scaling-stroke"" stroke-width=""1"" style=""display:${$('lCad').checked?'':'none'}""/>`);
 world.insertAdjacentHTML('beforeend','<g id=""gIss""></g>');
 const a=f.align;$('fmeta').innerHTML=`<b>${esc(f.floor)}</b> → Revit level <b>${esc(f.level)}</b> · CAD: ${esc(f.cad)}${f.title?' ['+esc(f.title)+']':''}<br>`+
  (a?`Alignment: ${esc(a.method)} · ${a.match}% of CAD walls on Revit walls${a.rms!=null?' · RMS '+a.rms+' mm':''} ${a.ok?'<span style=""color:var(--ok)"">✓ reliable</span>':'<span class=""sev-Critical"">⚠ uncertain</span>'}<br>`:'')+
  esc(f.units||'')+(f.notes&&f.notes.length?'<br>'+f.notes.map(esc).join('<br>'):'');
 const keys=Object.keys(f.cadCounts||{});
 $('fcounts').innerHTML='<tr><th>Element</th><th>CAD</th><th>Revit</th></tr>'+keys.map(k=>`<tr><td>${k}</td><td>${f.cadCounts[k]}</td><td>${(f.rvtCounts||{})[k]??''}</td></tr>`).join('');
 fit();drawIssues();
}
const COL={Critical:'var(--crit)',Major:'var(--major)',Minor:'var(--minor)',Info:'var(--info)'};
function drawIssues(){
 const g=$('gIss');if(!g)return;const f=D.floors[curFloor];if(!f)return;
 const ids=new Set(filtered().map(i=>i.id));
 g.style.display=$('lIss').checked?'':'none';
 g.innerHTML=D.issues.filter(i=>i.floor===f.floor&&i.x!=null&&ids.has(i.id)&&i.st!=='Resolved').map(i=>{
  const col=COL[i.sev],sel=i.id===selId;
  return `<g data-id=""${esc(i.id)}"" style=""cursor:pointer""><title>${esc(i.id+' — '+i.title)}</title>`+
  (i.path?`<path d=""${pathD(i.path)}"" stroke=""${col}"" stroke-width=""${sel?5:3}"" fill=""none"" vector-effect=""non-scaling-stroke""/>`:'')+
  `<circle cx=""${i.x}"" cy=""${-i.y}"" r=""${i.r}"" fill=""${col}"" fill-opacity=""${sel?.35:.14}"" stroke=""${col}"" stroke-width=""${sel?4:2}"" vector-effect=""non-scaling-stroke""/></g>`}).join('');
}
$('world').addEventListener('click',e=>{const t=e.target.closest('g[data-id]');if(t&&!dragged)select(t.dataset.id,false)});
['lCad','lRvt','lGrid','lIss'].forEach(id=>$(id).onchange=()=>{const m={lCad:'gCad',lRvt:'gRvt',lGrid:'gGrid',lIss:'gIss'};const el=$(m[id]);if(el)el.style.display=$(id).checked?'':'none'});
// pan & zoom
let drag=null,dragged=false;
svg.addEventListener('wheel',e=>{e.preventDefault();const r=svg.getBoundingClientRect();const mx=vb[0]+(e.clientX-r.left)/r.width*vb[2],my=vb[1]+(e.clientY-r.top)/r.height*vb[3];
 const s=e.deltaY>0?1.2:1/1.2;vb=[mx-(mx-vb[0])*s,my-(my-vb[1])*s,vb[2]*s,vb[3]*s];setVB()},{passive:false});
svg.addEventListener('pointerdown',e=>{drag={x:e.clientX,y:e.clientY,vb:vb.slice(),id:e.pointerId};dragged=false});
svg.addEventListener('pointermove',e=>{if(!drag)return;const r=svg.getBoundingClientRect();const dx=(e.clientX-drag.x)/r.width*vb[2],dy=(e.clientY-drag.y)/r.height*vb[3];
 if(!dragged&&Math.abs(e.clientX-drag.x)+Math.abs(e.clientY-drag.y)>3){dragged=true;try{svg.setPointerCapture(drag.id)}catch(_){}}vb=[drag.vb[0]-dx,drag.vb[1]-dy,vb[2],vb[3]];setVB()});
svg.addEventListener('pointerup',()=>{drag=null;setTimeout(()=>dragged=false,0)});
window.addEventListener('resize',fit);
if(D.floors.length){showFloor(0)}else{$('viewer').innerHTML='<p class=""muted"" style=""padding:16px"">No floor was compared. See the run log below.</p>'}
renderList();
</script></body></html>";
    }
}
