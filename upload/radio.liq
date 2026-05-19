set("server.telnet", true)
set("server.telnet.port", 8080)
set("server.telnet.bind_addr", "0.0.0.0")
set("log.level", 4)

# Activa el cálculo automático de metadata para crossfade
# IMPORTANTE: sin argumentos, se llama una vez al inicio
enable_autocue_metadata()

icecast_host = getenv("localhost","ICECAST_HOST")
icecast_port = int_of_string(default=8000,getenv("8000","ICECAST_PORT"))
icecast_pass = getenv("hackme","ICECAST_PASSWORD")
icecast_mount = getenv("/radio","ICECAST_MOUNT")

music_queue = request.queue()
offair = single("/scripts/offair.ogg")
radio = fallback(track_sensitive=false, [music_queue, offair])

# Crossfade de 3 segundos (fade in/out)
radio = crossfade(fade_in=3., fade_out=3., radio)

output.icecast(
  %ogg(%vorbis(quality=0.3)),
  host=icecast_host,
  port=icecast_port,
  password=icecast_pass,
  mount=icecast_mount,
  name="Jellyfin Radio Online",
  mksafe(radio)
)

def cmd_status(x) = "running" end
def cmd_append(x) = music_queue.push(request.create(x)); "added" end
def cmd_skip(x) = music_queue.skip(); "skipped" end
def cmd_clear(x) = music_queue.set_queue([]); "cleared" end
def cmd_queue_length(x) = string(music_queue.length()) end
# Guardar nombre del track actual y notificar al plugin de Jellyfin via on_track
current_track_name = ref("")
music_queue.on_track(synchronous=false, fun(m) ->
  begin
    fname = list.assoc(default="", "filename", m)
    current_track_name := fname
    fname_encoded = string.replace(pattern=" ", fun(_) -> "%20", fname)
    ignore(http.get("http://localhost:8096/RadioOnline/TrackChange?path=" ^ fname_encoded))
  end
)
def cmd_current_track(x) = current_track_name() end

server.register("queue.status", cmd_status)
server.register("queue.append", cmd_append)
server.register("queue.skip", cmd_skip)
server.register("queue.clear", cmd_clear)
server.register("queue.length", cmd_queue_length)
server.register("queue.current_track", cmd_current_track)