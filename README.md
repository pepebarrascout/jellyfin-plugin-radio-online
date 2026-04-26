# Jellyfin Radio Online Plugin
<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-radio-online/main/logo.png" height="180"/><br />
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-radio-online/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/pepebarrascout/jellyfin-plugin-radio-online/total?color=9b59b6&label=descargas"/></a>
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-radio-online/issues"><img alt="GitHub Issues" src="https://img.shields.io/github/issues/pepebarrascout/jellyfin-plugin-radio-online?color=9b59b6"/></a>
        <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11.x-blue.svg"/></a>
        <a href="https://icecast.org/"><img alt="Icecast" src="https://img.shields.io/badge/Icecast-Streaming-green?logo=icecast&logoColor=white"/></a>
    </p>
</div>

> **Radio en linea automatizada** desde Jellyfin. Transmite audio a un servidor Icecast con programacion semanal de playlists, gestion inteligente de horarios y llenado automatico con musica aleatoria.

**Requiere Jellyfin version `10.11.0` o superior.**

---

## ✨ Caracteristicas

| Caracteristica | Descripcion |
|---|---|
| 📡 **Streaming a Icecast** | Transmite audio en tiempo real a cualquier servidor Icecast compatible |
| 🎵 **Formatos Dual** | Soporte para OGG (Vorbis) y M4A (AAC) con bitrate configurable (32-320 kbps) |
| 📅 **Programacion Semanal** | Asigna playlists a dias y horarios especificos (Lunes a Viernes, 00:00-23:59) |
| ✂️ **Recorte Inteligente** | Si la playlist excede el horario asignado, se corta automaticamente |
| 🔀 **Llenado Aleatorio** | Si la playlist es mas corta que el horario, el resto se llena con musica aleatoria |
| 🎲 **Musica por Defecto** | Horarios y dias sin programacion se llenan con musica aleatoria de tu biblioteca |
| 🔁 **Repeticion Semanal** | Todas las programaciones se repiten automaticamente cada semana |
| 🖥️ **Panel de Configuracion** | Dashboard web integrado en Jellyfin para gestion completa del plugin |
| 📊 **Monitor de Estado** | API REST para consultar el estado del streaming en tiempo real |
| 🏷️ **Metadatos de Stream** | Nombre, descripcion y genero del stream para los oyentes de Icecast |

---

## 🚀 Instalacion

### Metodo 1: Desde el Catalogo de Plugins de Jellyfin (via Manifest) ⭐ Recomendado

1. En tu servidor Jellyfin, navega a **Panel de Control > Plugins > Repositorios**
2. Haz clic en el boton **+** (agregar repositorio)
3. Ingresa los siguientes datos:
   - **Nombre**: `Radio Online`
   - **URL del Manifest**:
     ```
     https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-radio-online/main/manifest.json
     ```
4. Haz clic en **Guardar**
5. Navega a la pestana **Catalogo**
6. Busca **Radio Online** en la lista de plugins disponibles
7. Haz clic en **Instalar**
8. Reinicia Jellyfin cuando se te solicite

### Metodo 2: Instalacion Manual

1. Descarga la ultima version desde [Releases](https://github.com/pepebarrascout/jellyfin-plugin-radio-online/releases)
2. Descomprime el archivo ZIP
3. Copia todos los archivos `.dll` a la carpeta de plugins de tu servidor Jellyfin:
   - **Linux**: `~/.config/jellyfin/plugins/`
   - **Windows**: `%LocalAppData%\Jellyfin\plugins\`
   - **macOS**: `~/.local/share/jellyfin/plugins/`
   - **Docker**: Monta un volumen en `/config/plugins` dentro del contenedor
4. Reinicia Jellyfin

---

## ⚙️ Configuracion

### Paso 1: Configurar el Servidor Icecast

Antes de configurar el plugin, asegurate de tener un servidor Icecast corriendo y configurado para aceptar conexiones de fuente (source).

#### Paso 1.1: Configurar fallback-mount (REQUERIDO para transiciones sin cortes)

Para que los oyentes no se desconecten al cambiar de un programa a otro, debes configurar un **mount de silencio (fallback)** en Icecast. Cuando un programa termina y el siguiente aun no ha comenzado, el plugin envia audio de silencio a este mount secundario, y Icecast mueve automaticamente a los oyentes ahi. Cuando el nuevo programa inicia, los oyentes regresan al mount principal sin interrupcion.

En tu archivo de configuracion `icecast.xml`, agrega lo siguiente dentro de la seccion `<icecast>`:

```xml
<mount>
    <mount-name>/radio</mount-name>
    <fallback-mount>/radio-silence</fallback-mount>
    <charset>UTF-8</charset>
</mount>

<mount>
    <mount-name>/radio-silence</mount-name>
    <charset>UTF-8</charset>
</mount>
```

**Nota importante:**
- El mount principal (`/radio`) debe coincidir con el **Punto de Montaje** que configures en el plugin.
- El mount de silencio (`/radio-silence`) se genera automaticamente agregando `-silence` al nombre del mount principal.
- Si usas un mount diferente (ej: `/stream`), el mount de silencio sera `/stream-silence`.
- El plugin se encarga de iniciar y detener el FFmpeg de silencio automaticamente durante las transiciones, por lo que no necesitas gestionarlo manualmente.
- **Si no configuras el fallback-mount, los oyentes se desconectaran brevemente al cambiar de programa.**

Despues de modificar `icecast.xml`, reinicia el servidor Icecast para aplicar los cambios.

#### Paso 1.2: Configurar la conexion en el plugin

1. Navega a **Panel de Control > Plugins > Radio Online**
2. En la seccion **Icecast Server Configuration**, completa los campos:
   - **Server URL**: URL completa de tu servidor Icecast (ej: `http://tu-servidor:8000`)
   - **Username**: Usuario de fuente Icecast (generalmente `source`)
   - **Password**: Contrasena de fuente Icecast
   - **Mount Point**: Punto de montaje (ej: `/radio`, `/stream`). Debe coincidir con el mount-name del fallback-mount en tu `icecast.xml`.
   - **Audio Format**: `ogg` (Vorbis) o `m4a` (AAC)
   - **Audio Bitrate**: Bitrate de codificacion en kbps (recomendado: 96-192)

### Paso 2: Configurar Jellyfin

3. En la seccion **Jellyfin Settings**, selecciona un **usuario de biblioteca**. Este usuario debe tener acceso a las bibliotecas de musica y playlists que se usaran en la programacion.
4. Activa la casilla **Enable Radio** para iniciar la automatizacion.

### Paso 3: Programar Horarios

5. En la seccion **Weekly Schedule**, haz clic en **+ Add Schedule Entry**
6. Completa los campos:
   - **Day**: Dia de la semana (Lunes a Viernes)
   - **Start Time** / **End Time**: Rango horario en formato 24h (HH:mm)
   - **Playlist**: Selecciona una playlist de Jellyfin, o deja en "Random Music"
   - **Display Name**: Nombre descriptivo del bloque (ej: "Morning Show")
7. Haz clic en **Save Entry**
8. Repite para todos los bloques horarios deseados
9. Haz clic en **Save Configuration** para aplicar

### Opciones de Configuracion

| Opcion | Descripcion |
|---|---|
| **Server URL** | URL del servidor Icecast sin barra final (ej: `http://192.168.1.100:8000`) |
| **Username** | Usuario de fuente Icecast (default: `source`) |
| **Password** | Contrasena de fuente configurada en Icecast |
| **Mount Point** | Punto de montaje, debe comenzar con `/` (ej: `/radio`) |
| **Audio Format** | `ogg` (Vorbis, recomendado) o `m4a` (AAC) |
| **Audio Bitrate** | Entre 32 y 320 kbps. Recomendado: 128 kbps |
| **Stream Name** | Nombre visible para los oyentes en Icecast |
| **Stream Description** | Descripcion del stream |
| **Stream Genre** | Genero musical del stream |
| **Public Stream** | Si esta activo, el stream aparece en directorios publicos de Icecast |
| **Library User** | Usuario de Jellyfin con acceso a la biblioteca de musica |
| **Enable Radio** | Activa/desactiva la automatizacion de radio |

---

## 🔄 Como Funciona

El plugin opera como un servicio en segundo plano dentro del servidor Jellyfin, ejecutando un ciclo continuo de streaming:

```
┌─────────────────────────────────────────────────────────────┐
│                     Jellyfin Server                         │
│                                                              │
│  ┌──────────────┐    ┌──────────────────┐                   │
│  │ Config Page   │    │ Schedule Manager │                   │
│  │ (Dashboard)   │    │ Service          │                   │
│  └──────┬───────┘    └────────┬─────────┘                   │
│         │                     │                              │
│         ▼                     ▼                              │
│  ┌──────────────┐    ┌──────────────────┐                   │
│  │ Plugin Config │    │ Audio Provider   │                   │
│  │               │    │ Service          │                   │
│  └──────┬───────┘    └────────┬─────────┘                   │
│         │                     │                              │
│         └──────────┬──────────┘                              │
│                    ▼                                         │
│  ┌──────────────────────────────────┐                       │
│  │   Radio Streaming Hosted Service  │                       │
│  │   (Ciclo principal en segundo    │                       │
│  │    plano - Background Service)    │                       │
│  └──────────────┬───────────────────┘                       │
│                 │                                             │
│                 ├─────────────────────┐                      │
│                 ▼                     ▼                      │
│  ┌──────────────────────┐  ┌────────────────────┐          │
│  │ FFmpeg (programa)    │  │ FFmpeg (silencio)  │          │
│  │ → mount /radio       │  │ → mount /radio-    │          │
│  │                      │  │   silence          │          │
│  └──────────┬───────────┘  └────────┬───────────┘          │
└─────────────┼────────────────────────┼──────────────────────┘
              │                        │
              ▼                        ▼
         ┌──────────────────────────────┐
         │       Icecast Server          │
         │  /radio ←→ /radio-silence     │
         │  (fallback-mount: transicion   │
         │   automatica entre mounts)     │
         └──────────────┬───────────────┘
                        │
                        ▼
               ┌────────────────┐
               │    Oyentes     │
               │ (Listeners)    │
               └────────────────┘
```

### Transicion entre Programas (Silence Bridge)

Cuando un programa termina y comienza otro, el plugin realiza una transicion sin cortes:

```
15s antes       │  Programa A termina  │  Silencio  │  Nuevo FFmpeg  │
                │                      │            │  conecta        │
────────────────┼──────────────────────┼────────────┼────────────────┤
FFmpeg A:  ████████████████████████████░            ░                ░
Silencio:  ░            ░░░░░░░░░░░░░░░░░░████████████░                ░
FFmpeg B:  ░            ░            ░░░░░░░░░░░░░░░░░░█████████████████
Oyentes:   ██████████████████████████████████████████████████████████████
           Escuchan A   Silencio 1-5s    Escuchan B
                        (imperceptible)
```

1. **15 segundos antes** de que termine el programa actual, el plugin inicia un segundo proceso FFmpeg que envia silencio al mount `/radio-silence`
2. Espera 5 segundos para que el FFmpeg de silencio se conecte a Icecast
3. **Al terminar el programa**, mata el FFmpeg principal. Icecast detecta la caida de `/radio` y **mueve automaticamente a los oyentes** a `/radio-silence` (gracias al `fallback-mount`)
4. Los oyentes escuchan **1-5 segundos de silencio** (imperceptible, como una pausa natural)
5. El plugin inicia el **nuevo FFmpeg** para el siguiente programa en `/radio`
6. Espera a que se conecte, y luego **detiene el FFmpeg de silencio**
7. Icecast mueve a los oyentes de vuelta a `/radio`

**Resultado: Los oyentes NUNCA se desconectan. Solo escuchan un breve silencio natural entre programas.**

### Flujo de Decision por Programa

1. El servicio verifica la **programacion semanal** para el momento actual
2. Si hay un **horario activo** con playlist asignada:
   - Reproduce la playlist en orden
   - Si la playlist **excede** el tiempo del slot → se **recorta** automaticamente
   - Si la playlist **termina antes** → se llena con **musica aleatoria**
3. Si **no hay programacion** activa → reproduce **musica aleatoria** de la biblioteca
4. Antes de cada pista, **re-verifica** la programacion por si cambio de slot
5. Los **fines de semana** (Sabado y Domingo) siempre reproducen musica aleatoria
6. Todo se **repite semanalmente** con la misma programacion

---

## ⏰ Ejemplo de Programacion

| Dia | Hora | Playlist | Comportamiento |
|---|---|---|---|
| Lunes | 08:00 - 12:00 | "Noticias Matutinas" | Se reproduce la playlist completa |
| Lunes | 12:00 - 14:00 | "Musica para Almorzar" | Se reproduce la playlist |
| Lunes | 14:00 - 18:00 | (sin asignar) | Musica aleatoria de la biblioteca |
| Lunes | 18:00 - 22:00 | "Drive Time" | Se recorta si excede las 4 horas |
| Martes - Viernes | 08:00 - 22:00 | "Programa Semanal" | Se repite de Lunes a Viernes |
| Sabado - Domingo | (todo el dia) | (sin asignar) | Musica aleatoria las 24 horas |

---

## 🔧 Solucion de Problemas

### El plugin no aparece en el panel de control
- Verifica que el archivo `.dll` este en la carpeta correcta de plugins
- Asegurate de reiniciar Jellyfin despues de copiar los archivos
- Revisa los logs de Jellyfin para errores de carga del plugin

### No se puede conectar a Icecast
- Verifica que el servidor Icecast este corriendo y acepte conexiones
- Revisa que la URL, usuario, contrasena y mount point sean correctos
- Asegurate de que el mount point inicie con `/` (ej: `/radio`)
- Verifica que Jellyfin tenga acceso de red al servidor Icecast

### El streaming no inicia
- Verifica que la casilla **Enable Radio** este activada
- Asegurate de haber seleccionado un **usuario de biblioteca** valido
- Verifica que el usuario tenga acceso a bibliotecas con contenido de audio
- Revisa los logs de Jellyfin para errores del streaming service

### Las playlists no se reproducen
- Verifica que las playlists contengan archivos de audio validos
- Asegurate de que los archivos de audio sean accesibles desde el servidor Jellyfin
- Comprueba que los archivos tengan ruta de archivo valida (no solo URLs de streaming)

### La musica aleatoria no funciona
- Verifica que exista contenido de audio en la biblioteca del usuario configurado
- Asegurate de que la biblioteca contenga archivos con extensiones de audio soportadas
- Revisa los logs para errores del Audio Provider Service

---

## 🛠️ Compilacion

### Requisitos Previos
- [.NET SDK 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

### Pasos para Compilar

```bash
# Clonar el repositorio
git clone https://github.com/pepebarrascout/jellyfin-plugin-radio-online.git
cd jellyfin-plugin-radio-online

# Compilar en modo Release
dotnet build --configuration Release

# El archivo compilado estara en:
# Jellyfin.Plugin.RadioOnline/bin/Release/net9.0/Jellyfin.Plugin.RadioOnline.dll
```

Los archivos `.dll` resultantes se copian a la carpeta de plugins de Jellyfin.

---

## 🏗️ Arquitectura

| Archivo | Responsabilidad |
|---|---|
| `Plugin.cs` | Entry point del plugin. Registro de paginas web del dashboard |
| `ServiceRegistrator.cs` | Registro de servicios en el contenedor DI de Jellyfin |
| `Services/RadioStreamingHostedService.cs` | Servicio en segundo plano: ciclo principal de streaming |
| `Services/IcecastStreamingService.cs` | Gestion de FFmpeg y envio de audio a Icecast |
| `Services/ScheduleManagerService.cs` | Logica de programacion semanal y deteccion de slots activos |
| `Services/AudioProviderService.cs` | Acceso a biblioteca Jellyfin, playlists y musica aleatoria |
| `ScheduledTasks/RadioSchedulerTask.cs` | Tarea programada visible en el dashboard de Jellyfin |
| `Api/RadioOnlineController.cs` | API REST para estado del streaming y listado de playlists |
| `Configuration/PluginConfiguration.cs` | Modelo de configuracion (persistencia XML automatica) |
| `Configuration/ScheduleEntry.cs` | Modelo de entrada de programacion (dia, hora, playlist) |
| `Configuration/configPage.html` | Pagina de configuracion del dashboard de Jellyfin |
| `Configuration/configPage.js` | Logica JavaScript del panel de configuracion |

---

## 💬 Soporte y Contribuciones

- **Reportes de bugs y sugerencias**: Usa la seccion de [Issues](https://github.com/pepebarrascout/jellyfin-plugin-radio-online/issues) para reportar problemas o proponer nuevas funciones
- **Contribuciones**: Las contribuciones son bienvenidas. No dudes en enviar un Pull Request
- **Version Alpha**: El plugin se encuentra en version Alpha y no se ha probado ampliamente. Agradecemos los **reportes de uso** para ir depurando el codigo. ¡Ayudanos a probar el plugin!

---

## ⚠️ Disclaimer

Este plugin es un proyecto independiente y no esta afiliado, respaldado ni patrocinado por Jellyfin. Jellyfin es una marca registrada de [The Jellyfin Project](https://jellyfin.org/).

---

## 📄 Licencia

Este proyecto esta bajo la licencia [MIT](LICENSE).
