# Jellyfin Radio Online Plugin
<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-radio-online/main/logo.png" height="180"/><br />
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-radio-online/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/pepebarrascout/jellyfin-plugin-radio-online/total?color=9b59b6&label=descargas"/></a>
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-radio-online/issues"><img alt="GitHub Issues" src="https://img.shields.io/github/issues/pepebarrascout/jellyfin-plugin-radio-online?color=9b59b6"/></a>
        <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11.x-blue.svg"/></a>
        <a href="https://www.liquidsoap.info/"><img alt="Liquidsoap" src="https://img.shields.io/badge/Liquidsoap-Streaming-orange?logo=liquidsoap&logoColor=white"/></a>
    </p>
</div>

> **Radio en linea automatizada** desde Jellyfin usando Liquidsoap como motor de streaming. Transmite audio a un servidor Icecast con programacion semanal de playlists. Liquidsoap se ejecuta en un contenedor Docker separado y el plugin lo controla via Telnet.

**Requiere Jellyfin version `10.11.0` o superior y un contenedor Docker de Liquidsoap.**

---

## ✨ Caracteristicas

| Caracteristica | Descripcion |
|---|---|
| 🎛️ **Motor Liquidsoap** | Usa Liquidsoap como motor de streaming profesional en contenedor Docker separado |
| 📡 **Streaming a Icecast** | Transmite audio en tiempo real a cualquier servidor Icecast compatible |
| 🎵 **Multi-formato** | Liquidsoap lee cualquier formato (MP3, M4A, OGG, FLAC) y codifica a OGG Vorbis |
| 📅 **Programacion Semanal** | Asigna playlists a dias y horarios especificos (Lunes a Domingo, 00:00-23:59) |
| 🔀 **Reproduccion Aleatoria** | Opcion de shuffle por programa - mezcla el orden de canciones |
| 📡 **Control via Telnet** | El plugin envia comandos Telnet a Liquidsoap (append, skip, clear, status) |
| 🔁 **Repeticion Semanal** | Todas las programaciones se repiten automaticamente cada semana |
| 🖥️ **Panel de Configuracion** | Dashboard web integrado en Jellyfin con edicion inline |
| 📊 **Monitor de Estado** | Estado del streaming y conexion Liquidsoap en tiempo real |

---

## 📋 Requisitos

Antes de instalar el plugin, necesitas:

1. **Jellyfin 10.11.0+** corriendo en tu servidor
2. **Liquidsoap** instalado en un contenedor Docker (ve [LIQUIDSOAP.md](LIQUIDSOAP.md) para instrucciones)
3. **Icecast** como servidor de streaming (puede estar en otro servidor)
4. **Biblioteca de musica** accesible desde el contenedor Liquidsoap

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

## 🐳 Instalacion de Liquidsoap (Requerido)

El plugin requiere Liquidsoap corriendo en un contenedor Docker. Consulta la guia completa de instalacion:

👉 **[LIQUIDSOAP.md - Guia de Instalacion de Liquidsoap](LIQUIDSOAP.md)**

Resumen rapido:
1. Instalar Liquidsoap con `docker compose up -d`
2. Crear el script `radio.liq` con los comandos Telnet
3. Configurar las variables de entorno de Icecast
4. Verificar la conexion Telnet en `localhost:8080`

---

## ⚙️ Configuracion

### Paso 1: Configurar Liquidsoap

Sigue la [guia de instalacion de Liquidsoap](LIQUIDSOAP.md) para preparar el contenedor y verificar la conexion Telnet.

### Paso 2: Configurar el Plugin

1. Navega a **Panel de Control > Plugins > Radio Online**
2. En la seccion **Servidor Liquidsoap**:
   - **Host Telnet**: `localhost` (si estan en el mismo servidor)
   - **Puerto Telnet**: `8080` (el puerto configurado en radio.liq)
3. En la seccion **Mapeo de Rutas**:
   - **Ruta de Biblioteca Jellyfin**: La ruta donde Jellyfin almacena los archivos (ej: `/media`)
   - **Ruta en Liquidsoap**: La ruta correspondiente en el contenedor (ej: `/music`)
4. En la seccion **Jellyfin**:
   - Selecciona un **usuario de biblioteca** con acceso a musica y playlists
   - Activa la casilla **Activar radio automatizada**

### Paso 3: Programar Horarios

5. En la pestana **Programacion**, haz clic en **Agregar Programa**
6. Completa los campos:
   - **Dia**: Dia de la semana
   - **Hora**: Rango horario
   - **Playlist**: Selecciona una playlist de Jellyfin
   - **Aleatorio**: Activa para mezclar el orden de canciones
   - **Nombre**: Nombre descriptivo del programa
7. Haz clic en el boton de guardar (check verde) para confirmar
8. Haz clic en **Guardar Programacion** al final de la pagina

### Opciones de Configuracion

| Opcion | Descripcion |
|---|---|
| **Host Telnet** | Host del servidor Telnet de Liquidsoap (default: `localhost`) |
| **Puerto Telnet** | Puerto Telnet de Liquidsoap (default: `8080`) |
| **Ruta Biblioteca Jellyfin** | Ruta raiz de archivos multimedia en el host (ej: `/media`) |
| **Ruta en Liquidsoap** | Ruta correspondiente dentro del contenedor (ej: `/music`) |
| **Usuario de Biblioteca** | Usuario de Jellyfin con acceso a musica y playlists |
| **Activar radio** | Activa/desactiva la automatizacion de radio |

---

## 🔄 Como Funciona

El plugin opera como un servicio en segundo plano que monitorea la programacion semanal y envia canciones a Liquidsoap via Telnet:

```
┌──────────────────────────────────────────────────────┐
│                  Jellyfin Server                      │
│                                                       │
│  ┌──────────────┐    ┌──────────────────┐            │
│  │ Config Page   │    │ Schedule Manager │            │
│  │ (Dashboard)   │    │ Service          │            │
│  └──────┬───────┘    └────────┬─────────┘            │
│         │                     │                       │
│         ▼                     ▼                       │
│  ┌──────────────────────────────────────┐            │
│  │   Radio Streaming Hosted Service      │            │
│  │   (Monitorea programacion semanal)    │            │
│  └──────────────┬───────────────────────┘            │
│                 │ Telnet (TCP)                          │
└─────────────────┼──────────────────────────────────────┘
                  │
                  ▼
┌──────────────────────────────────────────────────────┐
│           Contenedor Liquidsoap (Docker)              │
│                                                       │
│  ┌──────────────┐    ┌──────────────────┐            │
│  │ Telnet Server │    │ request.queue()  │            │
│  │ (puerto 8080)│    │ Cola de canciones│            │
│  └──────────────┘    └────────┬─────────┘            │
│                               │                       │
│                               ▼                       │
│                      ┌──────────────────┐            │
│                      │ output.icecast   │            │
│                      │ (OGG Vorbis)     │            │
│                      └────────┬─────────┘            │
└───────────────────────────────┼──────────────────────┘
                                │
                                ▼
                    ┌──────────────────────┐
                    │   Servidor Icecast   │
                    │   /radio             │
                    └──────────┬───────────┘
                               │
                               ▼
                      ┌────────────────┐
                      │    Oyentes     │
                      └────────────────┘
```

### Flujo de Operacion

1. El servicio verifica la **programacion semanal** para el momento actual
2. Si hay un **horario activo** con playlist asignada:
   - Obtiene las canciones de la playlist desde la biblioteca de Jellyfin
   - Traduce las rutas (`/media/...` → `/music/...`)
   - Aplica shuffle si esta habilitado
   - Limpia la cola de Liquidsoap y envia todas las canciones via Telnet
3. Si el horario **cambia** (nuevo programa o fin del programa):
   - Limpia la cola y carga las nuevas canciones
4. Si **no hay programacion** activa:
   - Limpia la cola y Liquidsoap reproduce silencio automaticamente
5. Todo se **repite semanalmente** con la misma programacion

---

## ⏰ Ejemplo de Programacion

| Dia | Hora | Playlist | Aleatorio | Comportamiento |
|---|---|---|---|---|
| Lunes | 08:00 - 12:00 | "Noticias Matutinas" | No | Se reproduce la playlist en orden |
| Lunes | 12:00 - 14:00 | "Musica para Almorzar" | Si | Se mezcla el orden de canciones |
| Lunes | 18:00 - 22:00 | "Drive Time" | No | Se reproduce la playlist |
| Martes - Viernes | 08:00 - 22:00 | "Programa Semanal" | No | Se repite de Lunes a Viernes |
| Sabado - Domingo | (sin programar) | -- | -- | Silencio (Liquidsoap con mksafe) |

---

## 🔧 Solucion de Problemas

### El plugin no aparece en el panel de control
- Verifica que el archivo `.dll` este en la carpeta correcta de plugins
- Asegurate de reiniciar Jellyfin despues de copiar los archivos

### No se puede conectar a Liquidsoap
- Verifica que el contenedor Liquidsoap este corriendo: `docker ps | grep liquidsoap`
- Verifica los logs: `docker logs liquidsoap --tail 20`
- Prueba la conexion Telnet: `echo "queue.status" | nc -w 3 localhost 8080`
- Verifica que `network_mode: host` este configurado en docker-compose.yml

### La musica no se reproduce
- Verifica que las rutas esten correctas: `docker exec liquidsoap ls /music`
- Verifica que el formato de audio sea soportado (MP3, M4A, OGG, FLAC)
- Revisa los logs de Jellyfin para ver las rutas que se envian a Liquidsoap

### Las playlists no se cargan
- Verifica que el usuario de biblioteca tenga acceso a playlists
- Verifica que las playlists contengan archivos de audio validos con rutas de archivo locales

---

## 🛠️ Compilacion

### Requisitos Previos
- [.NET SDK 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

### Pasos para Compilar

```bash
git clone https://github.com/pepebarrascout/jellyfin-plugin-radio-online.git
cd jellyfin-plugin-radio-online
dotnet build --configuration Release
```

Los archivos `.dll` resultantes se copian a la carpeta de plugins de Jellyfin.

---

## 🏗️ Arquitectura

| Archivo | Responsabilidad |
|---|---|
| `Plugin.cs` | Entry point del plugin |
| `ServiceRegistrator.cs` | Registro de servicios en DI |
| `Services/LiquidsoapClient.cs` | Cliente Telnet para Liquidsoap (TCP) |
| `Services/RadioStreamingHostedService.cs` | Servicio en segundo plano: monitorea programacion y envia tracks |
| `Services/ScheduleManagerService.cs` | Logica de programacion semanal |
| `Services/AudioProviderService.cs` | Acceso a playlists de Jellyfin |
| `Api/RadioOnlineController.cs` | API REST para estado y playlists |
| `Configuration/PluginConfiguration.cs` | Modelo de configuracion |
| `Configuration/ScheduleEntry.cs` | Modelo de entrada de programacion |
| `Configuration/config.html` | Dashboard de configuracion |

---

## 💬 Soporte y Contribuciones

- **Reportes de bugs y sugerencias**: Usa la seccion de [Issues](https://github.com/pepebarrascout/jellyfin-plugin-radio-online/issues)
- **Contribuciones**: Las contribuciones son bienvenidas. No dudes en enviar un Pull Request
- **Version Alpha**: El plugin se encuentra en version Alpha. Agradecemos los **reportes de uso** para ir depurando el codigo.

---

## ⚠️ Disclaimer

Este plugin es un proyecto independiente y no esta afiliado, respaldado ni patrocinado por Jellyfin o Liquidsoap.

---

## 📄 Licencia

Este proyecto esta bajo la licencia [MIT](LICENSE).
