import {ui, metadataLocale} from "./locale";
import {activeProfileId, getProfilePref, invokeProfile, writeProfilePref} from "./profile-context";
import type {CatalogItem} from "./main";

interface Candidate {id:number;name:string;year:number|null}

export function metadataControls(item:CatalogItem, mal:boolean, changed:(item:CatalogItem)=>Promise<void>) {
  const section=document.createElement("details");
  const summary=document.createElement("summary");
  const provider=mal?"MyAnimeList":"TMDb";
  summary.textContent=ui("Actualizar ficha con ")+provider;
  const form=document.createElement("form");
  function field(label:string,type:string,value:string){
    const wrap=document.createElement("label");wrap.className="field";wrap.textContent=label;
    const input=document.createElement("input");input.type=type;input.value=value;wrap.append(input);form.append(wrap);return input;
  }
  const query=field(ui("Título"),"search",item.name);query.required=true;query.maxLength=200;
  const credential=field(mal?ui("ID de cliente de MyAnimeList"):ui("Clave de API de TMDb"),"password","");
  credential.autocomplete="off";credential.placeholder=ui("Opcional si ya está configurada en el equipo");
  const submit=document.createElement("button");submit.textContent=ui("Buscar ediciones");form.append(submit);
  const status=document.createElement("p");status.setAttribute("role","status");
  const list=document.createElement("div");list.className="button-row";
  section.append(summary,form,status,list);
  let revision=0;
  form.oninput=()=>{revision++;list.replaceChildren();status.textContent="";submit.disabled=false;};
  form.onsubmit=async(event)=>{
    event.preventDefault();const token=++revision;list.replaceChildren();submit.disabled=true;status.textContent=ui("Buscando…");
    try{
      const hits=await invokeProfile<Candidate[]>("metadata_search",{id:item.syncId,language:getProfilePref(activeProfileId(),"metadata-language") || metadataLocale,credential:credential.value,query:query.value});
      if(token!==revision||!section.isConnected)return;
      status.textContent=hits.length?ui("Elegí la edición para actualizar la ficha."):ui("Sin resultados.");
      for(const hit of hits){
        const select=document.createElement("button");select.textContent=hit.year?`${hit.name} (${hit.year})`:hit.name;
        select.onclick=async()=>{
          list.inert=true;form.inert=true;
          try{
            const updated=await invokeProfile<CatalogItem>("metadata_apply",{id:item.syncId,external:hit.id,language:getProfilePref(activeProfileId(),"metadata-language") || metadataLocale,credential:credential.value});
            credential.value="";if(section.isConnected)await changed(updated);
          }catch(e){status.textContent=String(e);}
          finally{list.inert=false;form.inert=false;}
        };
        list.append(select);
      }
    }catch(e){if(token===revision)status.textContent=String(e);}
    finally{if(token===revision)submit.disabled=false;}
  };
  return section;
}


export function setupMetadataSettings() {
  const el = <T extends HTMLElement>(id:string) => document.getElementById(id) as T;
  const form = el<HTMLFormElement>("metadata-settings"), language = el<HTMLSelectElement>("metadata-language");
  const tmdb = el<HTMLInputElement>("metadata-tmdb"), mal = el<HTMLInputElement>("metadata-mal");
  const feedback = el("metadata-setting-status");
  async function update(save:boolean, clear = false) {
    try {
      form.inert = true;
      const result = await invokeProfile<{tmdb:boolean;mal:boolean;language:string|null}>("metadata_preferences", {
        language: save ? language.value : null,
        tmdb: clear ? "" : save && tmdb.value ? tmdb.value : null, mal: clear ? "" : save && mal.value ? mal.value : null
      });
      if (save) await writeProfilePref("metadata-language", language.value);
      language.value = result.language ?? getProfilePref(activeProfileId(),"metadata-language") ?? "";
      tmdb.value = mal.value = "";
      tmdb.placeholder = result.tmdb ? ui("Guardada") : ""; mal.placeholder = result.mal ? ui("Guardada") : "";
      feedback.textContent = save ? ui("Preferencias guardadas.") : "";
    } catch(e) { feedback.textContent = String(e); } finally { form.inert = false; }
  }
  form.onsubmit = e => { e.preventDefault(); void update(true); };
  el("metadata-clear").onclick = () => void update(true,true);
  el("settings-open").addEventListener("click",() => void update(false));
}
