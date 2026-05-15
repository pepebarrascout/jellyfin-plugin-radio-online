# Instalacion de Liquidsoap para Radio Online

Esta guia explica como instalar y configurar Liquidsoap en un contenedor Docker para funcionar con el plugin Radio Online de Jellyfin.

---

## Requisitos Previos

- Docker y Docker Compose instalados en el servidor donde corre Jellyfin
- Un servidor Icecast accesible (puede estar en otro servidor)
- La biblioteca de musica de Jellyfin accesible desde el servidor donde se instalara Liquidsoap

---

## Paso 1: Preparar la estructura de directorios

Crea los directorios necesarios en el servidor:

```bash
# Directorio para el script de Liquidsoap
mkdir -p /media/Cloud1/liquidsoap

# Generar archivo de silencio (5 segundos en OGG) - opcional
apt-get install -y sox
sox -n -r 44100 -c 2 /media/Cloud1/liquidsoap/silence.ogg trim 0 5
```

---

## Paso 2: Crear el archivo radio.liq

Crea el archivo `/media/Cloud1/liquidsoap/radio.liq` con el siguiente contenido:

```
set("server.telnet", true)
set("server.telnet.port", 8080)
set("server.telnet.bind_addr", "0.0.0.0")

icecast_host = getenv("localhost","ICECAST_HOST")
icecast_port = int_of_string(default=8000,getenv("8000","ICECAST_PORT"))
icecast_pass = getenv("hackme","ICECAST_PASSWORD")
icecast_mount = getenv("/radio","ICECAST_MOUNT")

music_queue = request.queue()
radio = mksafe(music_queue)

output.icecast(
  %ogg(%vorbis(quality=0.7)),
  host=icecast_host,
  port=icecast_port,
  password=icecast_pass,
  mount=icecast_mount,
  name="Jellyfin Radio Online",
  radio
)

def cmd_status(x) = "running" end
def cmd_append(x) = music_queue.push(request.create(x)); "added" end
def cmd_skip(x) = music_queue.skip(); "skipped" end
def cmd_clear(x) = music_queue.set_queue([]); "cleared" end

server.register("queue.status", cmd_status)
server.register("queue.append", cmd_append)
server.register("queue.skip", cmd_skip)
server.register("queue.clear", cmd_clear)
```

### Notas sobre el script

- **`set("server.telnet", true)`**: Habilita el servidor Telnet para que el plugin pueda enviar comandos
- **`set("server.telnet.port", 8080)`**: Puerto Telnet del servidor
- **`set("server.telnet.bind_addr", "0.0.0.0")`**: ESCENCIAL - Permite conexiones desde otros contenedores Docker. Sin esta linea, Telnet solo escucha en `127.0.0.1` y el plugin no puede conectarse desde un contenedor Jellyfin separado. El nombre correcto del setting es `server.telnet.bind_addr` (no `server.telnet.address` ni `server.telnet.host`)
- **`getenv("default","VAR_NAME")`**: Lee variables de entorno (el primer argumento es el valor por defecto)
- **`request.queue()`**: Cola de reproduccion que acepta canciones via Telnet
- **`mksafe()`**: Reproduce silencio automaticamente cuando la cola esta vacia
- **`%ogg(%vorbis(quality=0.7))`**: Codifica en OGG Vorbis con calidad 0.7 (equivalente a ~128kbps)
- **Los `server.register`**: Registran comandos Telnet que el plugin usa para controlar la cola

---

## Paso 3: Crear el archivo docker-compose.yml

Crea el archivo `docker-compose.yml` para Liquidsoap:

```yaml
services:
  liquidsoap:
    image: savonet/liquidsoap:v2.4.4
    container_name: liquidsoap
    command: ["/scripts/radio.liq"]
    network_mode: host
    environment:
      - ICECAST_HOST=192.168.1.100    # IP de tu servidor Icecast
      - ICECAST_PORT=8000                # Puerto de Icecast
      - ICECAST_PASSWORD=tu_password    # Contrasena de fuente Icecast
      - ICECAST_MOUNT=/radio             # Punto de montaje
    volumes:
      - /media/Music:/music:ro           # Biblioteca de Jellyfin
      - /media/Cloud1/liquidsoap:/scripts:ro  # Scripts de Liquidsoap
    restart: unless-stopped
```

### Campos a personalizar

| Campo | Descripcion | Ejemplo |
|---|---|---|
| `ICECAST_HOST` | IP o dominio del servidor Icecast | `192.168.1.100` |
| `ICECAST_PORT` | Puerto de Icecast (usualmente 8000) | `8000` |
| `ICECAST_PASSWORD` | Contrasena de fuente en Icecast | `tu_password` |
| `ICECAST_MOUNT` | Punto de montaje | `/radio` |
| `/media/Music:/music:ro` | Ruta de la biblioteca de Jellyfin | Debe coincidir con la ruta configurada en el plugin |

### Notas importantes

- **`network_mode: host`**: Liquidsoap comparte la red del host. Con `server.telnet.bind_addr` en `0.0.0.0`, el Telnet es accesible desde otros contenedores Docker a traves de la IP gateway del host.
- **`command: ["/scripts/radio.liq"]`**: NO uses `--enable-telnet` como argumento. El flag `--enable-telnet` puede sobreescribir la configuracion del script y forzar el bind a `127.0.0.1`. El script ya habilita Telnet con `set("server.telnet", true)`.
- **`:ro` en los volumes**: Los volumenes son de solo lectura por seguridad.
- El puerto Telnet es **8080**. Con `bind_addr` en `0.0.0.0`, es accesible desde cualquier interfaz de red del host.

---

## Paso 4: Iniciar Liquidsoap

```bash
cd /media/Cloud1/liquidsoap
docker compose up -d
```

Verificar que esta corriendo:

```bash
docker ps | grep liquidsoap
docker logs liquidsoap --tail 20
```

Deberia mostrar:
```
Connection setup was successful.
```

Verificar que Telnet escucha en todas las interfaces:

```bash
ss -tlnp | grep 8080
```

Debe mostrar `0.0.0.0:8080` (no `127.0.0.1:8080`). Si muestra `127.0.0.1`, revisa que el archivo `radio.liq` tenga `set("server.telnet.bind_addr", "0.0.0.0")` y que NO estes usando `--enable-telnet` en el command del docker-compose.

---

## Paso 5: Probar la conexion

### Verificar Telnet desde el host

```bash
echo "queue.status" | nc -w 3 localhost 8080
```

Deberia responder:
```
running
END
```

### Verificar Telnet desde el contenedor de Jellyfin

Si Jellyfin corre en otro contenedor Docker (bridge networking), usa la IP gateway de la red Docker:

```bash
# Primero encuentra la IP gateway de la red de Jellyfin (ej: 172.20.0.1)
# Se puede ver en Portainer o con: docker network inspect <nombre_red>

docker exec -it Jellyfin sh -c "echo 'queue.status' | nc -w 3 172.20.0.1 8080"
```

Deberia responder `running` + `END`. Si no responde, verifica que `ss -tlnp | grep 8080` muestra `0.0.0.0:8080`.

### Agregar una cancion

```bash
echo "queue.append /music/Tu-Album/Tu-Cancion.m4a" | nc -w 3 localhost 8080
```

Deberia responder:
```
added
END
```

### Verificar que la radio esta transmitiendo

Abre en tu navegador: `http://TU_IP_ICECAST:8000/radio`

---

## Configuracion en el Plugin

Una vez que Liquidsoap esta corriendo y la conexion Telnet funciona, configura el plugin en Jellyfin:

### Si Jellyfin y Liquidsoap estan en el mismo servidor

Jellyfin en Docker bridge y Liquidsoap en `network_mode: host`:

| Campo | Valor |
|---|---|
| **Liquidsoap Telnet Host** | La IP gateway de la red Docker de Jellyfin (ej: `172.20.0.1`). Se puede ver en Portainer en la seccion Networks del contenedor, o con `docker network inspect <red>` |
| **Liquidsoap Telnet Port** | `8080` |
| **Jellyfin Media Path** | La ruta donde Jellyfin almacena los archivos (ej: `/media`) |
| **Liquidsoap Music Path** | La ruta correspondiente dentro del contenedor Liquidsoap (ej: `/music`) |

### Si ambos estan en el mismo contenedor o red

| Campo | Valor |
|---|---|
| **Liquidsoap Telnet Host** | `localhost` o el nombre del contenedor |
| **Liquidsoap Telnet Port** | `8080` |

### Traduccion de rutas

La relacion de rutas es directa: el plugin reemplaza la ruta de Jellyfin con la ruta de Liquidsoap. Por ejemplo:
- Jellyfin ve: `/media/Music/ABBA/Bang-A-Boomerang.m4a`
- Plugin envia a Liquidsoap: `/music/Music/ABBA/Bang-A-Boomerang.m4a`

Esto corresponde al volume `/media/Music:/music:ro` en docker-compose.yml.

---

## Resolucion de Problemas

### El contenedor se reinicia constantemente

```bash
docker logs liquidsoap --tail 30
```

Busca errores de sintaxis en el script `.liq`. Los errores mas comunes:
- `Undefined variable queue`: Usa `request.queue()` en vez de `queue()` (Liquidsoap 2.x)
- `no method 'clear'`: Usa `music_queue.set_queue([])` en vez de `music_queue.clear()`
- `Cannot apply that parameter labeled "uri"`: Usa `music_queue.push(request.create(x))`
- `Cannot apply that parameter labeled "restart"`: Elimina `restart=true` de `output.icecast`
- `That source is fallible`: Aplica `mksafe()` antes de `output.icecast`, no despues de `crossfade`

### Telnet escucha solo en 127.0.0.1

Verifica con `ss -tlnp | grep 8080`:
- Si muestra `127.0.0.1:8080` → el `bind_addr` no se esta aplicando. Asegurate de:
  1. El archivo `radio.liq` tenga `set("server.telnet.bind_addr", "0.0.0.0")` (el nombre correcto es `bind_addr`, NO `address` ni `host`)
  2. NO estes usando `--enable-telnet` en el command del docker-compose (este flag puede sobreescribir el bind address del script)
  3. Reiniciar el contenedor: `docker restart liquidsoap`

### Telnet no responde desde el contenedor de Jellyfin

- Verifica que Telnet funciona desde el host: `echo "queue.status" | nc -w 3 localhost 8080`
- Verifica que `ss -tlnp | grep 8080` muestra `0.0.0.0:8080`
- Encuentra la IP gateway correcta de la red Docker de Jellyfin (Portainer → Networks)
- Prueba desde el contenedor: `docker exec -it Jellyfin sh -c "nc -zv <IP_GATEWAY> 8080"`

### Las canciones no se reproducen

- Verifica que las rutas en Liquidsoap son correctas: los archivos deben existir en `/music/...` dentro del contenedor
- Verifica que el volume esta montado correctamente: `docker exec liquidsoap ls /music`
- Los formatos soportados por Liquidsoap incluyen: MP3, M4A (AAC), OGG (Vorbis), FLAC, WAV

### Icecast desconecta a Liquidsoap

- Verifica la IP y puerto de Icecast en las variables de entorno
- Verifica la contrasena de fuente en Icecast
- Verifica que el punto de montaje no esta en uso por otra fuente

---

## Comandos Telnet de Referencia

| Comando | Descripcion | Ejemplo |
|---|---|---|
| `queue.status` | Obtener estado del servidor | `echo "queue.status" \| nc localhost 8080` |
| `queue.append /ruta/archivo.m4a` | Agregar cancion a la cola | `echo "queue.append /music/song.m4a" \| nc localhost 8080` |
| `queue.skip` | Saltar cancion actual | `echo "queue.skip" \| nc localhost 8080` |
| `queue.clear` | Limpiar toda la cola | `echo "queue.clear" \| nc localhost 8080` |
| `queue.current_track` | Obtener la ruta de la cancion reproduciendose actualmente | `echo "queue.current_track" \| nc localhost 8080` |

### Comando queue.current_track

El plugin usa `queue.current_track` para verificar periodicamente que la cancion que Jellyfin muestra en el dashboard coincide con lo que Liquidsoap esta reproduciendo realmente. Si se detecta una desincronizacion (por ejemplo, despues de una caida de conexion TCP), el plugin corrige su indice interno para que coincida con el estado real de Liquidsoap.

Para habilitar este comando, agrega esta linea a tu `radio.liq`:

```
def cmd_current_track(x) =
  source = music_queue.current()
  if source == null then
    "none"
  else
    request = source.annotate
    request.uri
  end
end
server.register("queue.current_track", cmd_current_track)
```

**Nota**: Si prefieres no agregar este comando, el plugin seguira funcionando pero no podra detectar ni corregir desincronizaciones automaticamente.
