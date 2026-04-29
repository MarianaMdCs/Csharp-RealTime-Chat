# Real-Time Chat App

Aplicación de mensajería instantánea desarrollada en **C# con .NET 8**, compuesta por un servidor TCP de consola y un cliente de escritorio WPF. Permite a múltiples usuarios chatear en tiempo real con soporte para mensajes privados, envío de imágenes y persistencia del historial de conversaciones.

Desarrollada como proyecto académico para la materia de *Current Programming*.

## Funcionalidades

- **Mensajería en tiempo real** – Comunicación instantánea vía TCP Sockets entre todos los clientes conectados.
- **Registro y autenticación** – Login automático: si el usuario no existe, se registra al instante. Contraseñas almacenadas con hash **SHA-256**.
- **Mensajes privados** – Selecciona un usuario de la lista para enviarle un mensaje solo visible entre ustedes dos.
- **Envío de imágenes** – Los usuarios pueden compartir imágenes de hasta **4 MB** en el chat.
- **Historial de chat** – Al conectarse, el cliente recibe automáticamente el historial de mensajes públicos y privados relevantes.
- **Lista de usuarios en línea** – El servidor notifica a todos los clientes cuando alguien se conecta o desconecta.
- **Monitor de conexiones** – El servidor detecta y limpia clientes caídos cada **10 segundos** con KeepAlive.
- **Persistencia** – Mensajes y usuarios almacenados en una base de datos local **SQLite** (`chat.db`).

## Stack

| Capa | Tecnología |
|------|-----------|
| Lenguaje | C# (.NET 8) |
| UI del cliente | WPF (Windows Presentation Foundation) · XAML |
| Servidor | Consola (.NET 8) · TCP Sockets (`System.Net.Sockets`) |
| Base de datos | SQLite (`System.Data.SQLite 1.0.116`) |
| Seguridad | SHA-256 para hash de contraseñas |
| Concurrencia | Threads por cliente + monitor de conexiones en background |

## Estructura del proyecto

```
Servidor/
└── ConsoleApp1/
    └── ConsoleApp1/
        └── Program.cs      # Servidor TCP + lógica de auth + SQLite

Cliente/
└── WpfApp1/
    └── WpfApp1/
        ├── MainWindow.xaml     # UI del cliente
        └── MainWindow.xaml.cs  # Lógica del cliente WPF
```

## Cómo usarlo

### Requisitos

- Windows con **.NET 8 SDK** instalado
- Visual Studio 2022 (recomendado) o `dotnet` CLI

### 1. Iniciar el servidor

Abre el proyecto `ConsoleApp1` (carpeta `Servidor`) y ejecútalo:

```bash
cd ConsoleApp1/ConsoleApp1
dotnet run
```

Verás en consola:

```
Chat Server started...
```

El servidor escucha en el puerto **5000**. La base de datos `chat.db` se crea automáticamente en la misma carpeta.

### 2. Abrir clientes

Abre el proyecto `WpfApp1` (carpeta `Cliente`) y ejecuta **dos o más instancias**:

```bash
cd WpfApp1/WpfApp1
dotnet run
```

O desde Visual Studio: click derecho → *Open in File Explorer* → ejecuta el `.exe` varias veces.

### 3. Iniciar sesión

Ingresa un nombre de usuario y contraseña en cada ventana. Si el usuario no existe, se registra automáticamente. Si ya existe, valida las credenciales.

## Protocolo de mensajes

El servidor y cliente se comunican con mensajes de texto delimitados por `|`:

| Tipo | Formato | Descripción |
|------|---------|-------------|
| `AUTH` | `AUTH\|usuario\|contraseña` | Login o registro |
| `TEXT` | `TEXT\|remitente\|destinatario\|mensaje` | Mensaje de texto (destinatario = `ALL` para público) |
| `IMAGE` | `IMAGE\|remitente\|destinatario\|datos\|tipo\|nombre` | Imagen en base64 (máx. 4MB) |
| `HISTORY` | `HISTORY\|remitente\|timestamp\|mensaje` | Historial al conectarse |
| `SYSTEM` | `SYSTEM\|server\|texto` | Notificación del servidor |

## Base de datos SQLite

Se crean automáticamente dos tablas:

```sql
CREATE TABLE Users (
    Id       INTEGER PRIMARY KEY,
    Username TEXT,
    Password TEXT  -- SHA-256 hash
);

CREATE TABLE Messages (
    Id        INTEGER PRIMARY KEY,
    Sender    TEXT,
    Message   TEXT,
    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
);
```

## Notas

- El servidor debe estar corriendo **antes** de abrir los clientes.
- Funciona localmente (`127.0.0.1:5000`); no requiere conexión a internet.
- Para simular una conversación real, abre múltiples instancias del `.exe` directamente.
- El archivo `chat.db` se genera en la carpeta de ejecución del servidor (`bin/Debug/net8.0/`).

## 👩‍💻 Autora

Desarrollado por **Mariana Mercado** — servidor TCP, lógica de autenticación, base de datos SQLite y cliente WPF.
