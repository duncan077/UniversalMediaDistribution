# Miniaturas del catálogo con Cloudflare

El servidor guarda miniaturas JPEG de hasta 360 × 540 píxeles en `catalog-thumbnails.db`, dentro de `UMD_RUST_SERVER_DATA_DIRECTORY`. Genera cada imagen cuando se solicita por primera vez y la reutiliza después de reiniciar. El límite predeterminado es de 2 GiB y se recupera espacio eliminando las miniaturas más antiguas. Como se pueden volver a generar, la base de miniaturas queda fuera del respaldo del servidor.

Activá el caché en el binario nuevo con un origen HTTPS público. Para el servicio de usuario de UMD, ejecutá `systemctl --user edit umd-server-rust.service` y agregá este bloque.

```ini
[Service]
Environment="UMD_SERVER_PUBLIC_URL=https://umd.duncan.com.ar"
Environment="UMD_SERVER_THUMBNAIL_CACHE_MIB=2048"
```

Reiniciá con `systemctl --user restart umd-server-rust.service` después de desplegar el binario. Las búsquedas, las últimas incorporaciones, la paginación y las fichas entregarán la URL de UMD a los clientes. El export completo conserva las referencias originales. Las portadas que ya tienen referencia quedan disponibles enseguida. Los títulos que carecen de ella la incorporan cuando el rastreador los vuelve a consultar.

## Regla de caché

En la zona `duncan.com.ar`, el registro de `umd.duncan.com.ar` debe estar en modo **Proxied**. Abrí **Cache Rules**, creá una regla llamada **UMD miniaturas del catálogo** y pegá esta expresión en **Edit expression**.

```text
(http.host eq "umd.duncan.com.ar"
 and http.request.method in {"GET" "HEAD"}
 and starts_with(http.request.uri.path, "/api/v1/catalog/thumbnails/")
 and http.request.uri.query eq "")
```

- En **Cache eligibility**, seleccioná **Eligible for cache**.
- En **Edge TTL**, seleccioná **Use cache-control header if present, bypass cache if not**. El servidor indica hasta 7 días de caché compartido y hasta 24 horas para el navegador. Si una miniatura vencida sigue disponible y el proveedor falla, la sirve con un plazo corto. Los errores llevan `Cache-Control: no-store`.
- En **Browser TTL**, seleccioná **Respect origin**. Dejá la clave de caché predeterminada y guardá con **Deploy**.
- Si ya hay una regla que evita el caché para toda `/api/`, dejá esta regla después en el orden de evaluación. La ruta específica debe prevalecer para las miniaturas.

[Opciones de Cache Rules](https://developers.cloudflare.com/cache/how-to/cache-rules/settings/) · [Orden de las reglas](https://developers.cloudflare.com/cache/how-to/cache-rules/order/)

## Verificación

```bash
miniatura=$(curl -fsS 'https://umd.duncan.com.ar/api/v1/catalog/recent' \
  | jq -r '[.items[].thumbnailRef | strings | select(startswith("https://umd.duncan.com.ar/api/v1/catalog/thumbnails/"))][0] // empty')
test -n "$miniatura"
for intento in 1 2; do
  curl -fsS -D - -o /dev/null "$miniatura" \
    | grep -Ei '^(content-type|cache-control|cf-cache-status|age):'
done
```

Una respuesta `CF-Cache-Status: HIT` confirma que Cloudflare entregó la imagen sin descargarla del servidor. Puede haber `MISS` en la primera solicitud, en otros centros de datos o después de que caduque una entrada. Revisá que la ruta sirva `image/jpeg` antes de buscar un `HIT`. Si necesitás retirar una portada ya cacheada, purgá su URL en Cloudflare. [Estados del caché](https://developers.cloudflare.com/cache/concepts/cache-responses/) · [Purga por URL](https://developers.cloudflare.com/cache/how-to/purge-cache/purge-by-single-file/)
