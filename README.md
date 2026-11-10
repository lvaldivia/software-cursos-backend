# Productos API

API .NET 10 para PostgreSQL o MySQL. Puerto 8080; health check `/health`.

## Configuracion por entorno

Copiar `.env.example` a `.env` y completar los valores. Docker carga el archivo
con `docker run --env-file .env -p 8080:8080 productos-api:local`.
No incluir credenciales en Git ni en la imagen. `dotnet run` no carga `.env`
automaticamente: exportar las variables en la terminal.

En ECS configurar DB_ENGINE, DB_HOST, DB_PORT, DB_USER, DB_PASSWORD, DB_NAME,
DB_MAX_POOL y CORS_ORIGINS en cada servicio. Cambiar variables requiere nuevas
tareas; no requiere reconstruir la imagen. Para contrasenas usar ECS Secrets
con Secrets Manager o Parameter Store y permisos del rol de ejecucion.

## Imagenes y versiones

GitHub Actions construye en cada PR y publica en GHCR al subir a main o un tag.
Cada imagen lleva `sha-<commit completo>` y etiquetas OCI de version, revision,
origen, proyecto y componente. Main agrega `edge`; `v1.2.0` agrega `1.2.0`.
Desplegar usando el digest `@sha256:...` para fijar exactamente la imagen.
GHCR es independiente de Docker Hub; las imagenes existentes no se modifican.
Verificar visibilidad del paquete: para imagen privada, configurar autenticacion
del registro en ECS antes de desplegar.

Build local:

```powershell
docker build --build-arg VERSION=1.2.0 --build-arg REVISION=$(git rev-parse HEAD) -t productos-api:1.2.0 .
```

## Revision pendiente

La ruta `/` muestra host y usuario de BD; los errores de BD incluyen detalles
del proveedor. Antes de exponer una API de produccion, limitar ese diagnostico,
configurar CORS_ORIGINS y agregar autenticacion a las operaciones de escritura.
