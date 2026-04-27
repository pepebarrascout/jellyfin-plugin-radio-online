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
mkdir -p /opt/liquidsoap/scripts

# Generar archivo de silencio (5 segundos en OGG)
apt-get install -y sox
sox -n -r 44100 -c 2 /opt/liquidsoap/scripts/silence.ogg trim 0 5
```

---

## Paso 2: Crear el archivo radio.liq

Crea el archivo `/opt/liquidsoap/scripts/radio.liq` con el siguiente contenido:

```
set("server.telnet", true)
set("server.telnet.port", 8080)

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
- **`set("server.telnet.port", 8080)`**: Puerto Telnet dentro del contenedor
- **`getenv("default","VAR_NAME")`**: Lee variables de entorno (el primer argumento es el valor por defecto)
- **`request.queue()`**: Cola de reproduccion que acepta canciones via Telnet
- **`mksafe()`**: Reproduce silencio automaticamente cuando la cola esta vacia
- **`%ogg(%vorbis(quality=0.7))`**: Codifica en OGG Vorbis con calidad 0.7 (equivalente a ~128kbps)
- **Los `server.register`**: Registran comandos Telnet que el plugin usa para controlar la cola

---

## Paso 3: Crear el archivo docker-compose.yml

Crea el archivo `/opt/liquidsoap/docker-compose.yml`:

```yaml
services:
  liquidsoap:
    image: savonet/liquidsoap:v2.4.4
    container_name: liquidsoap
    command: ["/scripts/radio.liq"]
    network_mode: host
    environment:
      - ICECAST_HOST=209.141.61.116    # IP de tu servidor Icecast
      - ICECAST_PORT=8000                # Puerto de Icecast
      - ICECAST_PASSWORD=tu_password    # Contrasena de fuente Icecast
      - ICECAST_MOUNT=/radio             # Punto de montaje
    volumes:
      - /ruta/a/tu/biblioteca:/music:ro           # Biblioteca de Jellyfin
      - /opt/liquidsoap/scripts:/scripts:ro        # Scripts de Liquidsoap
    restart: unless-stopped
```

### Campos a personalizar

| Campo | Descripcion | Ejemplo |
|---|---|---|
| `ICECAST_HOST` | IP o dominio del servidor Icecast | `209.141.61.116` |
| `ICECAST_PORT` | Puerto de Icecast (usualmente 8000) | `8000` |
| `ICECAST_PASSWORD` | Contrasena de fuente en Icecast | `tu_password` |
| `ICECAST_MOUNT` | Punto de montaje | `/radio` |
| `/ruta/a/tu/biblioteca:/music:ro` | Ruta de la biblioteca de Jellyfin | `/media:/music:ro` |

### Notas importantes

- **`network_mode: host`**: Liquidsoap usa Telnet en `127.0.0.1` y Docker bridge networking no permite acceder a este. `network_mode: host` comparte la red del host directamente.
- **`:ro` en los volumes**: Los volmenes son de solo lectura por seguridad.
- El puerto Telnet es **8080** dentro del contenedor. Con `network_mode: host`, es accesible desde `localhost:8080` en el host.

---

## Paso 4: Iniciar Liquidsoap

```bash
cd /opt/liquidsoap
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

---

## Paso 5: Probar la conexion

### Verificar Telnet

```bash
echo "queue.status" | nc -w 3 localhost 8080
```

Deberia responder:
```
running
END
```

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

Una vez que Liquidsoap esta corriendo, configura el plugin en Jellyfin:

1. **Host Telnet**: `localhost` (si estan en el mismo servidor)
2. **Puerto Telnet**: `8080`
3. **Ruta de Biblioteca Jellyfin**: `/media` (la ruta donde Jellyfin almacena los archivos)
4. **Ruta en Liquidsoap**: `/music` (la ruta correspondiente dentro del contenedor)

La relacion de rutas es directa: el plugin reemplaza la ruta de Jellyfin con la ruta de Liquidsoap. Por ejemplo:
- Jellyfin ve: `/media/Music/ABBA/Bang-A-Boomerang.m4a`
- Plugin envia a Liquidsoap: `/music/Music/ABBA/Bang-A-Boomerang.m4a`

Esto corresponde al volume `/media:/music:ro` en docker-compose.yml.

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

### Telnet no responde (Connection reset)

- Verifica que el script tiene `set("server.telnet", true)` al inicio
- Verifica que `network_mode: host` esta configurado en docker-compose.yml
- Verifica que no hay otro servicio usando el puerto 8080: `ss -tlnp | grep 8080`

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
