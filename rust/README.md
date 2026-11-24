# UMD en Rust

Cliente Tauri para escritorio, Android y Android TV, con un servidor Rust independiente.
El cliente comparte la biblioteca SQLite, el reproductor y los lectores entre plataformas.

## Ejecutar escritorio

Requiere Rust 1.93 o posterior, Node.js 22.12 o posterior y los
[requisitos de Tauri 2](https://v2.tauri.app/start/prerequisites/).
En Windows utiliza WebView2 y las herramientas de compilación MSVC.

Desde la raíz del repositorio:

```powershell
npm --prefix rust/desktop ci
npm --prefix rust/desktop run tauri dev
```

La biblioteca ofrece importación y exportación del catálogo JSON de UMD,
búsqueda sin distinguir acentos, filtros por medio, favoritos persistentes,
detalles y descarga paginada de un catálogo remoto.
Los archivos JSON exportados por la aplicación .NET se pueden abrir desde
**Importar catálogo**. Los capítulos, temporadas, fuentes, derechos y demás
metadatos de cada título se conservan al exportarlos nuevamente.

La aplicación utiliza una biblioteca SQLite propia en el directorio de datos
del identificador `org.universalmediadistribution.rust`. El JSON de catálogo
intercambia los títulos y sus metadatos, sin preferencias personales. Los
respaldos de cuenta llevan el estado personal y las fichas a las que hace referencia.

### Reproducir y leer

En **Buscar en proveedores**, buscá en la biblioteca, el servidor conectado y los proveedores habilitados a la vez, o elegí un sitio. Podés dar prioridad a tus proveedores favoritos y ocultar las fichas identificadas como contenido para adultos. **Agregar** guarda la edición y sus capítulos o temporadas. Mientras la app está abierta, revisa diariamente las ediciones guardadas de los proveedores habilitados. En CapibaraTraductor también podés elegir la traducción por el nombre del grupo.

En la ficha, elegí un capítulo y pulsá **Leer en…**, o elegí un episodio y pulsá **Ver en…** para seleccionar su fuente. Las páginas y los enlaces de reproducción se consultan al abrir la unidad. El vínculo con el proveedor se conserva al exportar e importar el catálogo en otra instalación Rust. FramexTV admite HLS, MP4, DASH y las pistas de subtítulos publicadas por el sitio. Cuando la fuente requiere la página del proveedor, **Ver en UMD** abre una ventana separada con bloqueo de anuncios en Windows (WebView2), Linux (WebKitGTK) y Android/TV. **Abrir en el navegador** usa el navegador del sistema y sus propios filtros.

En **TV y video online → Bloqueo de anuncios del reproductor** podés elegir **Reglas de UMD** (predeterminado, sin conexión), **Reglas de UMD y listas en línea**, **Reglas completas** o **Desactivado**. Las listas en línea incorporan filtros de red, reglas de ocultamiento por sitio y acciones compatibles de uBlock Origin. El modo completo agrega la lista de molestias de EasyList. UMD guarda las listas en el equipo y revisa las de más de siete días. Con poca memoria usa menos reglas. Con ahorro de energía, batería baja o conexión medida pospone las descargas. Para aplicar las nuevas reglas de ocultamiento, volvé a abrir el reproductor del proveedor.

La primera vez, UMD toma el idioma del sistema si es español o inglés. En **Perfiles → Idioma** podés cambiarlo. TMDb recibe el idioma elegido tanto en la búsqueda como al descargar la ficha. La biblioteca y la ficha muestran el título en el idioma elegido cuando el catálogo trae esa traducción. Si falta, muestran el nombre principal.

Abrí la ficha de un título y elegí el capítulo, episodio o pista. **Abrir archivo**
permite seleccionar audio, video, EPUB, PDF o un cómic CBZ/ZIP o CBR/RAR. Para manga suelto,
**Carpeta de imágenes** ordena las páginas por número, por ejemplo 1, 2, 10.
El lector permite avanzar con los botones, las flechas del teclado o el número
de página. Recuerda la página y la posición de reproducción por título y unidad.

Si una fuente del catálogo enlaza a un archivo CBR, CBZ o EPUB, podés abrirlo
desde la ficha con **Leer CBR**, **Leer CBZ** o **Leer EPUB**. En Android TV
esta opción también funciona en equipos sin selector de archivos. Admite
el servidor de catálogo conectado, incluso en la red local, y enlaces públicos HTTP(S).

Para fuentes de video online, UMD reconoce reproductores HTML5 y Video.js, páginas que declaran la URL del video y páginas que delegan a un reproductor incrustado. Puede reproducir MP4, HLS y DASH y mostrar las pistas de subtítulos que publica la fuente. En las fuentes de proveedores también podés abrir el reproductor web si el sitio requiere interacción.

En el reproductor podés elegir el motor web o VLC. Android y Android TV también ofrecen el reproductor del sistema. Si una fuente falla, podés cambiar de motor o volver a elegir la fuente. Los ZIP de Windows y Linux y el APK de Android incluyen LibVLC y sus módulos. Los controles de posición, audio, subtítulos y sala están junto al video. En Windows con captura de audio por proceso, UMD retransmite el audio de WebView2 desde el proceso de la aplicación para que se pueda compartir su ventana con sonido.
También abre fuentes HTTP(S) directas presentes en `filePath`, incluyendo HLS.
Las fuentes remotas deben permitir el acceso del reproductor; PDF y HLS pueden
requerir CORS. **TV y video online** abre listas M3U y permite consultar las grillas de CableVisionHD, TV por Internet HD y TV Libre Fútbol. Elegí el canal para buscar la señal directa o abrir la página del reproductor. UMD también reconoce reproductores Zilla con una lista HLS disponible. Si un proveedor pide verificación, abrí su sitio desde el aviso, completá el paso indicado y volvé a intentar la consulta.
**Descargas** admite enlaces magnet v1 o híbridos y archivos torrent. Permite
pausar, reanudar y elegir los archivos que querés descargar. En **Ver archivos**, los videos y el audio compatibles admiten reproducción durante la descarga. Los libros y cómics se pueden abrir cuando termina.
Si un torrent incluye un único `info.json`, **Ver archivos → Importar catálogo del torrent** incorpora las fichas y vincula sus archivos y secuencias de imágenes a esa descarga.

Los archivos y magnets marcados como privados usan una sesión separada con trackers HTTPS. El cliente comprueba el hash y el indicador privado antes de descargar los archivos. Las rutas que requieren autenticación leen la dirección autorizada de una variable local, por ejemplo `UMD_TRACKER_ENDPOINT_HTTPS_TRACKER_EXAMPLE` para `https://tracker.example`. Si el catálogo declara una clave de credencial, se usa esa clave en el sufijo. La dirección autorizada debe tener el mismo origen que la del catálogo. La cola privada dura hasta que se cierra UMD. Para continuar más tarde, volvé a agregar la descarga. Los archivos ya descargados se verifican y reutilizan.
El lector EPUB presenta las secciones en el orden indicado por el libro e incluye las imágenes locales.
El lector permite elegir entre páginas y desplazamiento continuo y guarda esa preferencia por título en el respaldo. En lectura continua podés activar **Desplazamiento automático** y elegir una velocidad de 0,25× a 10×. Con el contenido enfocado, Enter o el botón central del control remoto inicia o pausa el avance. Con el ratón, hacé clic con la rueda y mové el puntero hacia arriba o abajo para controlar la dirección y la velocidad. Girar la rueda detiene el avance automático.
El lector abre el índice del EPUB y carga cada capítulo y sus imágenes a medida que los necesitás. Durante una descarga BitTorrent podés abrir PDF, EPUB y CBZ, o reproducir audio y video en VLC.

Los PDF se dibujan con PDF.js, sin ejecutar sus acciones o scripts.
Los recursos de fuentes y decodificación se incluyen en la compilación.

Solo los archivos elegidos en el diálogo del sistema se habilitan para el lector.
Los cómics se extraen a un directorio temporal con nombres generados, sin usar
las rutas del archivo. Las imágenes se muestran en orden numérico y se aplican
límites de 64 MiB por página y 512 MiB por cómic. Para CBR, elegí un archivo
de un solo volumen y sin contraseña. Las carpetas tampoco siguen enlaces simbólicos.

Una ficha de manga importada sin vínculo admite **Buscar capítulo en proveedores**. UMD compara el nombre y sus variantes en los seis sitios de lectura, verifica que la edición tenga ese capítulo y guarda el vínculo cuando obtiene las páginas.

Podés actualizar la descripción, portada, géneros, título original y nombres alternativos desde la ficha. Las películas y series usan TMDb, y el anime, manga y las novelas web usan MyAnimeList. Buscá por título y elegí la edición correcta. La credencial se puede ingresar para esa consulta o configurar localmente con `UMD_TMDB_API_KEY` o `UMD_MAL_CLIENT_ID`.

### Cuenta y respaldo

En **Perfiles → Respaldo de la biblioteca**, **Guardar copia completa** crea un archivo SQLite `.umddb` con el catálogo, los perfiles y sus preferencias. **Restaurar copia** valida la integridad del archivo y prepara la restauración para la próxima apertura de UMD. Antes de aplicarla guarda la biblioteca actual en el directorio de datos. Las descargas multimedia se almacenan por separado. Al iniciar, UMD comprueba la integridad de SQLite. Si encuentra daños, conserva la base original y sus archivos auxiliares y abre una biblioteca nueva para que puedas restaurar una copia.


**Restaurar copia** también acepta el respaldo SQLite `.umdbackup` del cliente .NET (esquemas 1 a 15). Importa títulos, fuentes, temporadas y capítulos, y conserva los favoritos, las listas, las preferencias de lectura y el progreso de cada perfil. Las sesiones de cuenta, los ajustes locales, el historial de títulos eliminados y las descargas quedan fuera de la conversión.

**Perfil** muestra quién está usando la aplicación. Desde ese botón se pueden
crear, renombrar, cambiar y eliminar perfiles. Todos ven los mismos títulos. Cada
perfil conserva sus favoritos, listas, progreso, modo de lectura y sesión de cuenta.
La aplicación recuerda el perfil seleccionado después de cerrarla. Al actualizar,
el perfil **Principal** conserva los datos personales de la biblioteca existente.

**Cuenta y respaldo** permite iniciar sesión, guardar un respaldo y revisar su
fecha y cantidad de favoritos antes de restaurarlo. La sesión se recuerda por
perfil hasta que cierres sesión o venza el acceso. Fuera de localhost, el inicio
de sesión requiere HTTPS.

**Guardar respaldo** combina los cambios personales con la revisión de la
cuenta. **Restaurar** reemplaza el estado personal de ese perfil en una sola
transacción y agrega a la biblioteca las fichas necesarias para abrir esos
favoritos, listas y posiciones.

En **Cuenta y respaldo**, **Sincronizar ahora** combina los favoritos, listas,
miembros de listas, preferencias de lectura y progreso del perfil con los de la
misma cuenta en otro equipo. Las entradas personales llevan versiones. Si dos
equipos modifican la misma posición, prevalece la versión más reciente según el
reloj de sincronización. Solo incorpora las fichas de los títulos vinculados a
esos registros personales que falten en la biblioteca. Los archivos descargados
permanecen en el dispositivo.

**Sincronizar automáticamente durante esta sesión** sincroniza al activar la
opción, cada minuto mientras la app está visible y al volver a la app o recuperar
la conexión. El diálogo de cuenta permite revisar un respaldo sin que una
sincronización periódica cambie la revisión en pantalla. Si el servidor detecta
que otro equipo acaba de guardar, el cliente vuelve a leer y combinar los cambios.
Con un servidor Rust o .NET que admite sincronización incremental, la app
descarga el respaldo al iniciar sesión y después intercambia los registros
modificados y las fichas a las que hacen referencia. Un cambio de posición
envía ese título. Una consulta sin novedades no genera una escritura.
La app vuelve al respaldo completo si faltan identidades, se necesita reconciliar
el catálogo o el servidor solo admite respaldos completos. Al cerrar la sesión,
la siguiente conexión vuelve al modo manual.

El cliente .NET de este repositorio también puede iniciar sesión en el servidor
Rust, crear el primer respaldo y consultar sus cambios. Si el usuario borra el
respaldo en el servidor Rust, la revisión se conserva. Las tareas automáticas
respetan la eliminación. En .NET, **Sincronizar ahora** permite volver a crearlo.

Para sincronizar un catálogo con enlaces HTTP(S) directos en títulos o capítulos,
el servidor .NET debe incluir la actualización que admite fuentes sin torrent.
Las instalaciones anteriores pueden devolver HTTP 400 al subir esos catálogos.

El documento usa `universal-media-distribution.user-sync` (versiones 1 a 4).
Una exportación Rust con favoritos y progreso fue aceptada por el validador real
de .NET. La sincronización une ediciones coincidentes entre dispositivos, conserva capítulos y fuentes, y aplica las redirecciones a favoritos, listas, preferencias y progreso. Dos fichas con distintos años de estreno o con un nombre ambiguo en el mismo catálogo mantienen sus identidades.
Desde **Mis listas** se pueden crear y renombrar listas, agregar o quitar títulos y eliminar listas.
Los respaldos conservan estos cambios y las preferencias de lectura recibidas.

Para compartir una lista, iniciá sesión en **Cuenta y respaldo** y elegí
**Mis listas → Compartir lista → Publicar enlace**. La app sincroniza el perfil
con su cuenta y publica las fichas y fuentes de los títulos seleccionados.
El enlace refleja los cambios de la lista después de sincronizar. Desde
**Mis enlaces publicados** podés copiarlo de nuevo o retirarlo.

Quien recibe el enlace puede pegarlo en **Mis listas → Abrir enlace**, revisar
los títulos y guardar una copia en su perfil, incluso sin cuenta. La copia conserva
las fichas que ya existían en su biblioteca. Sigue disponible si el propietario
retira el enlace.

`UMD_RUST_DESKTOP_DATA_DIRECTORY` permite elegir una biblioteca separada para
desarrollo y pruebas.

**Conectar servidor** guarda la dirección después de comprobarla con una consulta de un solo resultado. Inicio muestra hasta doce incorporaciones recientes. Con un servidor anterior muestra una selección del catálogo. La búsqueda y las categorías consultan resúmenes bajo demanda. **Ver más del servidor** solicita la página siguiente. Abrir una ficha permite consultarla y **Agregar a mi biblioteca** guarda ese título para usarlo sin conexión. Los favoritos y la exportación JSON usan la biblioteca local.

Para generar el ZIP de escritorio, ejecutá en el sistema de destino, desde la raíz del proyecto:

```sh
python3 rust/desktop/package-zip.py
```

En Windows podés usar `py -3` en lugar de `python3`. El paquete se guarda en `artifacts/desktop/`. Para compilar el ZIP de Linux se usan las dependencias de desarrollo de Tauri y las herramientas del empaquetado de VLC de Debian (`eng/linux/bundle-vlc.py`), incluidos `patchelf`, `libvlc5`, `vlc-plugin-base` y `vlc-plugin-video-output`.

## Android y Android TV

Instalá los requisitos Android de Tauri, incluidos JDK, SDK, NDK y los destinos Rust de Android.
El proyecto Gradle se encuentra en `desktop/src-tauri/gen/android`. Para compilar un APK
de desarrollo e instalarlo en el dispositivo conectado desde la raíz del repositorio, ejecutá

```sh
npm --prefix rust/desktop ci
npm --prefix rust/desktop run tauri -- android build --debug --apk
adb install -r rust/desktop/src-tauri/gen/android/app/build/outputs/apk/universal/debug/app-universal-debug.apk
```

En Android se pueden elegir documentos o varias imágenes con el selector del sistema.
El mismo APK incluye una entrada para el lanzador de Android TV. Usá las flechas para
cambiar el foco, el botón central para seleccionar y Atrás para cerrar la pantalla activa.

## Ejecutar servidor

```powershell
cargo run --manifest-path rust/Cargo.toml -p umd-server
```

Escucha en `http://127.0.0.1:9005` y guarda la base en `rust-server-data/catalog.db`,
relativa al directorio desde el que se ejecuta. Variables de configuración:

| Variable | Uso |
| --- | --- |
| `UMD_RUST_SERVER_LISTEN` | Dirección y puerto, por defecto `127.0.0.1:9005` |
| `UMD_RUST_SERVER_DATA_DIRECTORY` | Directorio de almacenamiento |
| `UMD_SERVER_ADMIN_TOKEN` | Token de al menos 32 bytes para publicar el catálogo |
| `UMD_SERVER_ALLOW_REGISTRATION` | `true` habilita registro público; deshabilitado por defecto |
| `RUST_LOG` | Nivel de logs, por ejemplo `umd_server=info,tower_http=info` |

Con el token definido en el entorno del servidor y de la terminal que publica:

```powershell
Invoke-RestMethod -Method Put -Uri http://127.0.0.1:9005/api/v1/catalog/current `
  -Headers @{ Authorization = "Bearer $env:UMD_SERVER_ADMIN_TOKEN" } `
  -ContentType application/json -InFile docs/examples/torrent-info.json
```

La publicación reemplaza el catálogo en una única transacción. El servidor
comprueba el formato, la versión, los tipos de medio, los subtipos y las
identidades. El servidor admite 40 MiB por petición de catálogo o respaldo de forma predeterminada. Se puede ajustar con `UMD_SERVER_MAX_JSON_BYTES` entre 1 KiB y 256 MiB. La descarga del catálogo completo admite 30 MiB y cada página admite 8 MiB.

| Ruta | Comportamiento implementado |
| --- | --- |
| `GET /health` | Estado del proceso y hora del servidor |
| `GET /api/v1/capabilities` | Capacidades implementadas |
| `GET /api/v1/catalog/current` | Documento completo |
| `PUT /api/v1/catalog/current` | Reemplazo autenticado del catálogo |
| `GET /api/v1/catalog/metadata` | Revisión, fecha, SHA-256 y cantidad |
| `GET /api/v1/catalog/items` | Páginas con `cursor` y `limit` de 1 a 100 |
| `GET /api/v1/catalog/items/{id}` | Documento de un título |
| `GET /api/v1/catalog/search` | Búsqueda paginada con filtros de género, categoría, distribuidor, año, estado y subtipo. Admite `summary=true` |
| `POST /api/v1/admin/accounts` | Crear cuenta con token de administrador |
| `POST /api/v1/accounts/register` | Crear cuenta cuando el registro público está habilitado |
| `POST /api/v1/accounts/login` | Iniciar sesión con usuario y contraseña |
| `GET /api/v1/accounts/me` | Perfil de la sesión autenticada |
| `DELETE /api/v1/accounts/session` | Revocar la sesión actual |
| `POST /api/v1/shared-lists` | Publicar una lista del respaldo de la cuenta con `{"listId":"…"}` |
| `GET /api/v1/shared-lists` | Consultar los enlaces de la cuenta autenticada |
| `GET /api/v1/shared-lists/{shareId}` | Consultar el nombre, el propietario y la cantidad de títulos |
| `GET /api/v1/shared-lists/{shareId}/catalog` | Leer el catálogo público de esa lista |
| `DELETE /api/v1/shared-lists/{shareId}` | Retirar un enlace como propietario |
| `GET /api/v1/friends` | Ver amigos de la cuenta |
| `GET`, `POST /api/v1/friends/requests` | Ver solicitudes o enviar una por nombre de usuario |
| `POST /api/v1/friends/requests/{id}/accept`, `DELETE /api/v1/friends/requests/{id}` | Aceptar, rechazar o retirar una solicitud |
| `DELETE /api/v1/friends/{id}`, `PUT /api/v1/friends/{id}/sharing` | Quitar a un amigo o cambiar qué puede ver |
| `GET`, `PUT /api/v1/friends/visibility` | Consultar o cambiar la privacidad y los títulos ocultos |
| `GET /api/v1/friends/{id}/shared`, `GET /api/v1/friends/{id}/comparison` | Consultar títulos en común o comparar ambos respaldos |
| `POST /api/v1/friends/activity/lookup` | Consultar el progreso compartido para hasta 200 títulos |
| `POST /api/v1/watch-parties`, `POST /api/v1/watch-parties/join` | Crear una sala o unirse con su código |
| `GET`, `DELETE /api/v1/watch-parties/current` | Consultar la sala de la cuenta o salir de ella |
| `PUT /api/v1/watch-parties/current/options` | Reservar los controles para el anfitrión o permitirlos a todos |
| `GET /api/v1/watch-parties/socket` | Conectar el reproductor mediante WebSocket con la sesión de la cuenta |
| `GET`, `PUT /api/v1/releases/{canal}/{plataforma}/latest` | Consultar o publicar el manifiesto de la versión ofrecida |
| `GET`, `HEAD`, `PUT /api/v1/releases/{canal}/{plataforma}/{versión}/package/{archivo}` | Descargar, consultar o subir un paquete. Las descargas admiten rangos de bytes |
| `GET /api/v1/sync/snapshot` | Respaldo privado y revisión; documento nulo si no existe |
| `PUT /api/v1/sync/snapshot` | Guardar `{expectedRevision, document}` con control de concurrencia y soporte de `Content-Encoding: gzip` |
| `DELETE /api/v1/sync/snapshot` | Borrar con `{expectedRevision}` y conservar la revisión |
| `GET /api/v1/sync/changes?since=N` | Leer los cambios posteriores a N. Devuelve `full: true` cuando el cliente necesita el respaldo completo |
| `PATCH /api/v1/sync/snapshot/progress` | Combinar un documento parcial `{patch}` con el respaldo de la cuenta. Acepta gzip y un campo opcional `expectedRevision` |

Los contratos siguen los formatos de UMD .NET. Las cuentas usan SQLite separado
en `accounts.db`, contraseñas PBKDF2-SHA256 con 600.000 iteraciones y sal aleatoria,
tokens aleatorios guardados únicamente como SHA-256 y sesiones de 30 días. El administrador puede ajustar la duración con `UMD_SERVER_SESSION_DAYS` y el costo de las contraseñas nuevas con `UMD_SERVER_PASSWORD_ITERATIONS`.
El servidor limita los intentos de acceso a 8 por usuario cada 15 minutos y
procesa hasta 4 operaciones de cuentas en paralelo. Las peticiones de credenciales
tienen un límite de 8 KiB. El catálogo público admite 60 consultas por IP cada minuto de forma predeterminada, configurable mediante `UMD_SERVER_CATALOG_REQUESTS_PER_MINUTE`.

Para crear una cuenta desde una terminal administrativa:

```powershell
$account = @{username='usuario'; password=$env:UMD_NEW_ACCOUNT_PASSWORD; displayName='Usuario'}
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:9005/api/v1/admin/accounts `
  -Headers @{Authorization="Bearer $env:UMD_SERVER_ADMIN_TOKEN"} `
  -ContentType application/json -Body ($account | ConvertTo-Json)
```

Para compilar el servidor:

```powershell
cargo build --manifest-path rust/Cargo.toml --release -p umd-server
```

El servidor es independiente de Tauri y puede compilarse en Linux ARM64 con
Rust y un compilador C para SQLite. El binario de Windows no sirve para Linux.

### Migrar un servidor .NET y restaurar una copia

El ejecutable Rust incluye la migración del servidor .NET con esquema SQLite 2. Detené el servidor .NET y su rastreador para que los paquetes publicados y la base correspondan al mismo momento. Indicá el **directorio que contiene `server.db`** y un directorio de destino nuevo. La conversión conserva los identificadores del catálogo, las cuentas y sus contraseñas, las sesiones vigentes, los respaldos y sus revisiones, los enlaces públicos de listas, los permisos de amistad, la cola de rastreo y las publicaciones. Una cuenta migrada puede recibir un respaldo completo en su primera sincronización incremental. Las salas activas son temporales y se vuelven a crear después del cambio.

```sh
./umd-server --migrate-dotnet /ruta/datos-dotnet /ruta/datos-rust
UMD_RUST_SERVER_DATA_DIRECTORY=/ruta/datos-rust ./umd-server
```

El segundo comando sirve para comprobar el nuevo servidor. Al cambiar el servicio, configurá el nuevo directorio y conservá sus variables de entorno, incluida la clave de administración. La instalación .NET conserva su directorio original.

Para respaldar un servidor Rust, detené el servicio y el rastreador. La copia reúne las tres bases SQLite, los paquetes y los manifiestos de versiones. Guardala con el mismo cuidado que la base de cuentas. La restauración comprueba la integridad SQLite y los hashes SHA-256 de los archivos antes de publicar el directorio de destino. En ambos comandos elegí rutas de destino nuevas fuera del origen.

```sh
UMD_RUST_SERVER_DATA_DIRECTORY=/ruta/datos-rust ./umd-server --backup /ruta/copias/umd-01
./umd-server --restore /ruta/copias/umd-01 /ruta/datos-restaurados
```

### Administrar y rastrear el catálogo

Abrí `/admin` en el servidor Rust e ingresá la clave de `UMD_SERVER_ADMIN_TOKEN`. El panel muestra títulos, revisiones, memoria, errores, historial y actividad por proveedor. Desde ahí podés iniciar el rastreo completo o de un solo proveedor, reintentar fallos y guardar el intervalo entre consultas (1 a 60 segundos por proveedor).

El servidor guarda la cola, los intervalos y los puntos de reanudación en `crawler.db`, dentro de `UMD_RUST_SERVER_DATA_DIRECTORY`. Tanto el panel como el comando usan ese directorio. Los rastreos se pueden programar con la siguiente orden en el entorno del servicio:

```sh
./umd-server --scrape-once
```

Cada ejecución descubre hasta dos páginas por proveedor y procesa las fichas pendientes durante un máximo de 45 minutos por defecto. Respeta el tiempo de espera del proveedor y retoma el trabajo en la siguiente ejecución. Las fichas se incorporan al catálogo público conservando las fuentes que ya había en cada título o capítulo. Los registros del panel cubren las últimas 24 horas y hasta 10.000 eventos.

Los 17 lectores de catálogo se ejecutan dentro de `umd-server`. Los 16 sitios vienen configurados. Para conectar el lector de catálogos externos UMD, definí `UMD_CRAWLER_BASE_UMD_JSON_API` con una URL pública terminada en `/api/v1/catalog/`. El directorio de datos conserva la cola del rastreador Rust y sus puntos de reanudación. Los límites de páginas, duración e intervalos se describen en la [guía del rastreador](../docs/SERVER_CATALOG_CRAWLER.md).

DoramasFlix usa los mapas del sitio y las páginas públicas. Si disponés de una clave de plataforma propia para su API, podés configurar `UMD_DORAMASFLIX_PLATFORM_KEY` en el proceso de UMD. El cliente envía esa clave únicamente al servicio GraphQL de DoramasFlix.

### Publicar una actualización

El actualizador de UMD .NET puede usar el servidor Rust. Publicá primero el paquete con `PUT`, el token de administrador en `Authorization: Bearer …`, el tamaño en `Content-Length` y el SHA-256 hexadecimal en `X-UMD-SHA256`. El servidor devuelve la ruta de descarga en `relativePath`. Una carga interrumpida o con un hash incorrecto conserva el paquete anterior.

Después publicá el manifiesto en `latest` para ese canal y plataforma, con el mismo token. Usá la dirección HTTPS pública del paquete en `downloadUri`. El manifiesto incluye la versión, el hash, el tamaño y la firma ECDSA P-256 del hash generada por las herramientas de publicación de UMD. El cliente comprueba la firma con su clave pública de confianza antes de preparar la instalación.

Los paquetes y los manifiestos se guardan respectivamente en `packages/` y `releases/` dentro de `UMD_RUST_SERVER_DATA_DIRECTORY`. El límite por paquete es de 2 GiB y puede configurarse en bytes con `UMD_SERVER_MAX_PACKAGE_BYTES`. Si iniciás el servidor sin `UMD_SERVER_ADMIN_TOKEN`, las publicaciones existentes siguen disponibles para descarga y las cargas quedan deshabilitadas.

### Abrir y actualizar la app

Extraé el ZIP completo. En Windows abrí `umd-desktop.exe` con el runtime WebView2 disponible. En Linux abrí `./UMD` desde una sesión gráfica con GTK 3 y WebKitGTK 4.1. El ZIP de Linux conserva los permisos de ejecución al extraerlo con `unzip`.

En **Configuración → Actualizaciones**, **Descargar ZIP** permite guardar el archivo de una versión nueva. Cerrá la app y extraé el ZIP en otra carpeta. La biblioteca y los perfiles se conservan en el directorio de datos del usuario. Si usabas la versión MSI, abrí el ejecutable extraído para pasar al canal portátil.

El canal de escritorio es `tauri-portable`. Los nombres de los ZIP incluyen la versión y el destino, por ejemplo `UMD-0.1.10-win-x64.zip` y `UMD-0.1.10-linux-x64.zip`. El cliente comprueba la firma ECDSA P-256, el SHA-256, el tamaño y la identidad, versión y arquitectura declaradas dentro del ZIP. Android y Android TV siguen usando APK y el canal `tauri-stable`. La clave pública incorporada corresponde a `umd-release-1`. Para compilar una distribución con otra clave, definí `UMD_UPDATE_SIGNING_KEY_ID` y `UMD_UPDATE_SIGNING_PUBLIC_KEY` durante la compilación; la segunda variable contiene la clave pública SPKI DER en base64. La clave privada se usa en el equipo de publicación.

## Alcance y próximos pasos

El motor de torrents de esta versión no expone la política de cifrado entre pares de MonoTorrent (`Preferred`, `Required`, `Disabled`). Si tu instalación .NET exige cifrado entre pares, conservá ese cliente para esas descargas. El uso de HTTPS para contactar al tracker privado protege una conexión distinta.

La búsqueda de escritorio incluye los 16 sitios del rastreador. En **Configuración → Proveedores de contenido** podés agregar una API JSON compatible y editar el nombre o la dirección de un proveedor. El reproductor integrado admite fuentes HTML5, listas de canales M3U y configuraciones públicas compatibles. La disponibilidad de una edición o un video depende del proveedor. El cliente Tauri permite descargar actualizaciones verificadas para Windows y Linux e instalar APK en Android y Android TV.

En **Amigos** podés enviar solicitudes por nombre de usuario, aceptarlas o
retirarlas y comparar títulos con otro usuario del mismo servidor. La comparación
usa el último respaldo de cada cuenta. Compartir progreso está desactivado al
crear la cuenta. Podés habilitarlo para tus amigos, excluir a una persona u
ocultar un título desde **Actividad de amigos** en su ficha. Solo se muestra
la marca de favorito en títulos con progreso. La opción de recibir solicitudes
por nombre se controla por separado.

En **Sala de reproducción** podés crear una sala y compartir su código de seis caracteres con otra cuenta del mismo servidor. Para empezar con un título, abrilo en el reproductor de audio o video y creá la sala desde ahí. Cada participante abre el título en su biblioteca y elige una fuente. La sala comparte el título, la unidad y la posición. Las fuentes se reproducen en cada equipo. Al abrir **Ver en UMD** desde la ficha, la ventana del proveedor guarda el progreso para esa unidad y ofrece la misma sala en la ventana de la biblioteca. Puede leer y controlar el video de la página y de marcos del mismo origen. Un video dentro de un marco de otro sitio puede seguir reproduciéndose por su cuenta; la biblioteca indica si todavía no detectó un video accesible.

Cualquier participante puede reproducir, pausar o adelantar, salvo que el anfitrión active el control exclusivo. Si un participante necesita cargar el medio, la sala pausa y reanuda cuando está listo. Al salir el anfitrión, el siguiente participante pasa a controlar la sala. Reiniciar el servidor cierra sus salas.

## Verificar

```powershell
cargo test --manifest-path rust/Cargo.toml --workspace
cargo fmt --manifest-path rust/Cargo.toml --all --check
npm --prefix rust/desktop run build
cargo check --manifest-path rust/Cargo.toml -p umd-desktop
```
