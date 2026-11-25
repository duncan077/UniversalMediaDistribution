# Rastreador del catálogo Rust

El servidor y el rastreador comparten el directorio definido en
`UMD_RUST_SERVER_DATA_DIRECTORY`. Las bases SQLite contienen el catálogo, las
cuentas y el estado del rastreador. Para migrar una instalación .NET, crear una
copia o restaurarla, seguí la [guía del servidor](../rust/README.md#migrar-un-servidor-net-y-restaurar-una-copia).

## Ejecutar el rastreador

Usá el mismo entorno que el servidor y ejecutá `./umd-server --scrape-once`.
El comando registra los resultados y termina sin abrir un puerto. El panel
`/admin` muestra el estado, los errores y el intervalo de cada proveedor.
El servidor coordina los rastreos manuales y programados para evitar que se
superpongan.

El rastreador busca actualizaciones, recorre páginas del directorio de cada
proveedor y conserva sus puntos de reanudación. Guarda metadatos de títulos y
de sus episodios o capítulos. La extracción de videos y de imágenes de lectura
ocurre en el cliente. Los proveedores pueden responder con límites de frecuencia
o errores temporales. El rastreador aplaza los intentos según el estado registrado.

## Configuración

| Variable | Valor inicial y rango |
| --- | --- |
| `UMD_RUST_SERVER_DATA_DIRECTORY` | El mismo directorio de datos del servidor |
| `UMD_CRAWLER_PAGES_PER_PROVIDER` | 2 páginas por proveedor. Entre 1 y 100 |
| `UMD_CRAWLER_MAX_MINUTES` | 45 minutos por ejecución. Entre 1 y 50 |
| `UMD_CRAWLER_OPERATION_SECONDS` | 180 segundos por operación de descubrimiento. Entre 10 y 1200 |
| `UMD_CRAWLER_DETAIL_SECONDS` | 600 segundos por título. Entre 10 y 1200. Si configuraste el límite general, el límite por título toma ese valor por defecto |
| `UMD_CRAWLER_DISABLED_PROVIDERS` | IDs de proveedores separados por comas |
| `UMD_CRAWLER_BASE_<ID>` | URL pública opcional. Escribí el ID en mayúsculas y reemplazá los guiones por guiones bajos |

Para incorporar otro catálogo UMD, configurá `UMD_CRAWLER_BASE_UMD_JSON_API`
con la URL pública de su API de catálogo. Dejá esa variable vacía si el servidor
solo usa los proveedores incorporados.

## Programación en Linux

Copiá [el servicio](../eng/systemd/umd-catalog-crawler.service) y
[el temporizador](../eng/systemd/umd-catalog-crawler.timer) a
`~/.config/systemd/user/`. Ajustá la ubicación del ejecutable y del archivo
de entorno según tu instalación. Comprobá que ese archivo apunte al directorio
de datos del servidor Rust.

```sh
systemctl --user daemon-reload
systemctl --user enable --now umd-catalog-crawler.timer
systemctl --user list-timers umd-catalog-crawler.timer
journalctl --user -u umd-catalog-crawler.service
```
