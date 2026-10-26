# Preparación segura de una reparación de identidades

El modo de preparación **no modifica el catálogo ni aplica reparaciones**. Abre la base
original en modo de solo lectura, crea una copia consistente mediante SQLite
(incluye los cambios del WAL) y comprueba su integridad. Luego consulta de nuevo
los proveedores vinculados al título, sin reutilizar los nombres alternativos ni
los capítulos de la ficha mezclada para construir las propuestas.

Requiere .NET 11. Funciona con `catalog.db` de la app y con `server.db` del servidor.
El directorio de salida debe ser nuevo. La copia contiene datos privados, incluidas
posibles sesiones: no subirla a Git, al catálogo público ni compartirla.

```powershell
dotnet run --project eng/CatalogIdentityRepair -- "C:\Users\halod\AppData\Local\UniversalMediaDistribution\catalog.db" 019f9c98-664e-7a2d-8e53-bc1b0d1e6030 artifacts/catalog-repair/manual-damas-local
```

En Linux, usar el usuario propietario de la base (o ejecutar sobre una copia):

```sh
dotnet run --project eng/CatalogIdentityRepair -- /ruta/privada/server.db ID_DE_LA_FICHA artifacts/catalog-repair/servidor
```

Los ID locales y del servidor **pueden ser distintos**. El ID del servidor debe
salir de `items[].syncId` en la respuesta de `/api/v1/catalog/search`, no del nombre de un proveedor.
La herramienta respeta `UMD_CRAWLER_BASE_<PROVEEDOR>` y, para la base del servidor,
los intervalos de peticiones configurados en el admin. No solicita páginas de
lectura ni video. El catálogo UMD no cuenta como evidencia independiente: ya puede
contener la mezcla que se está investigando.

## Archivos generados

- `backup.sqlite`: copia completa y verificada; permite recuperar todo el estado.
- `plan.json`: título y capítulos anteriores, fuentes, respuestas nuevas por
  proveedor, errores y agrupaciones conservadoras propuestas. `applied` siempre
  es `false`. Un fallo de un proveedor no propone reemplazar sus datos por vacío.

## Aplicación posterior (deliberadamente no automática)

Se agregaron dos pasos explícitos para completar el flujo del servidor:

```sh
# Reintentar solo los fallos, buscando un ID canónico único si cambió el slug.
dotnet run --project eng/CatalogIdentityRepair -- --refresh-failures /ruta/plan.json
# Crear una NUEVA copia reparada, sin modificar la copia original ni producción.
dotnet run --project eng/CatalogIdentityRepair -- --apply-plan /ruta/plan.json /ruta/nueva/server-repaired.db
```

El segundo paso exige respuestas nuevas para todas las fuentes, verifica hashes,
agrupación e integridad, conserva el ID de la obra cuyo nombre principal ya tenía
la ficha y archiva los redirects ambiguos. Genera `.report.json` y `.catalog.sql`.
El SQL modifica únicamente las filas públicas implicadas y sus vínculos del
scraper; no reemplaza cuentas, sesiones ni documentos privados. Sus condiciones
de seguridad rechazan un catálogo afectado que cambió, vínculos diferentes,
reaplicaciones y un scraper con lease activo. No es una migración global.

Para producción se recomienda aplicar ese parche con `apply_catalog_patch.py`,
después de desplegar el fix y detener el servidor, cron y scraper. El script exige
`--apply`, crea un nuevo respaldo privado y revierte la transacción si falla una
condición. `verify_repair.py` compara una copia reparada contra el respaldo sin
mostrar datos privados. **No reemplazar toda la base activa con una copia antigua.**

1. Desplegar primero la corrección de identidades en servidor y clientes; detener
   el scraper y pausar sincronización/actualización durante la intervención.
2. Revisar **todas** las fuentes del ID mezclado en el servidor. Si falta alguna,
   no asumir que sus capítulos pertenecen a otra ficha; conservarla pendiente.
3. Confirmar los grupos con sus respuestas originales. Comparar los hashes y
   crear un respaldo nuevo si la base cambió desde la preparación.
4. En una transacción, reconstruir las fichas desde esas respuestas y reasignar
   los vínculos `crawler_titles` a la obra correcta. No reutilizar un redirect
   antiguo que ahora representaría varias obras distintas.
5. En cada cliente, conservar el ID del manga correcto, favoritos y listas. Los
   capítulos/progresos que no puedan atribuirse con seguridad deben quedar en una
   cuarentena recuperable: **no moverlos a otra obra solo por coincidir el número**.
   Las fuentes y el progreso también pueden usar ID de unidad: no borrarlos en
   cascada. Corregir el vínculo UMD mezclado y los nombres originales ajenos.
6. Verificar integridad, cantidades, favoritos, progreso y una sincronización de
   ida y vuelta antes de reactivar el scraper. No importar `plan.json` como si
   fuera un catálogo: el importador normal agrega capítulos y no separa mezclas.

Sin procedencia histórica por capítulo no existe una separación automática capaz
de saber, por ejemplo, a qué obra correspondía el progreso del capítulo 12. El
respaldo y la cuarentena preservan esa información sin inventar una atribución.
