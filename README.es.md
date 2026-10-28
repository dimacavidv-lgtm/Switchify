<h1>
  <img src="assets/logo.png" alt="" width="48" height="48" align="center">
  Switchify
</h1>

[English](README.md) · **Español**

Convierte archivos en Windows desde un menú circular. Arrastra un archivo, pulsa **Shift** y suéltalo sobre el formato que quieras, o simplemente deja el ratón encima de un archivo y pulsa Shift. El resultado se guarda junto al original, que nunca se sobrescribe.

Switchify es un único `.exe` que vive en la bandeja del sistema. Solo usa lo que ya trae Windows: no hace falta instalar otros programas, entornos ni navegadores.

<p align="center">
  <img src="assets/demo.gif" alt="Demostración de Switchify: convertir una imagen a JPG, un Markdown a PDF y abrir las herramientas de imagen" width="820">
</p>

## Cómo funciona

Hay cuatro formas de abrir el anillo:

| Gesto | Qué pasa |
|---|---|
| **Arrastrar un archivo + Shift** | Mientras arrastras uno o varios archivos, pulsa Shift. El anillo aparece bajo el cursor: suelta los archivos sobre una opción. |
| **Mantener Shift encima de un archivo** | Con el ratón quieto encima de un archivo, mantén Shift. Ve a una opción y suelta Shift para usarla. |
| **Toque de Shift encima de un archivo** | Un toque rápido abre el anillo y lo deja abierto: haz clic en una opción. Esc o un clic fuera lo cierran. |
| **Ctrl+Shift+Espacio** | Abre el anillo para los archivos seleccionados en el Explorador. |

Funciona en el Explorador de archivos y en el Escritorio. Si el archivo bajo el cursor forma parte de una selección, el anillo usa toda la selección.

### Qué puede hacer

| Archivos | Opciones |
|---|---|
| **Imágenes** (PNG, JPG, BMP, GIF, TIFF, ICO) | Convertir entre formatos, a PDF (varias imágenes → un PDF) y a texto con OCR. **Herramientas**: comprimir, recortar, fondo, editar, anotar, tapar y ver o quitar metadatos. |
| **Documentos** (MD, TXT, DOCX, HTML, PDF) | Convertir entre PDF, DOCX, MD, HTML y TXT. PDF a imágenes (una por página) o a texto con OCR. |
| **Vídeo** (MP4, MOV, MKV, AVI, WMV, WebM, 3GP) | Convertir a MP4 o WMV, comprimir y extraer el audio a MP3, M4A o WAV. |
| **Audio** (MP3, WAV, M4A, AAC, FLAC, WMA) | Convertir entre MP3, M4A, WAV, FLAC y WMA. |
| **Cualquier archivo** | Comprimir en ZIP (o extraer un ZIP). Las imágenes y los documentos también se pueden imprimir. |

## Instalación

Necesita **Windows 10 u 11**.

### Descarga

1. Descarga `Switchify.exe` de la [última versión](../../releases/latest).
2. Ponlo en la carpeta que quieras y ábrelo. Su icono aparece en la bandeja del sistema.

Puede que Windows SmartScreen avise porque la aplicación no está firmada: pulsa **Más información → Ejecutar de todas formas**.

### Compilar desde el código

No hace falta ningún SDK: se compila con el compilador de C# que ya trae Windows (.NET Framework 4).

```powershell
git clone https://github.com/dimacavidv-lgtm/Switchify.git
cd Switchify
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Esto crea `Switchify.exe` e instala una copia en `%LOCALAPPDATA%\Programs\Switchify`. Abre esa copia la primera vez; al volver a ejecutar `build.ps1` se actualiza (y se reinicia si está abierta).

### Iniciar con Windows

Haz clic derecho en el icono de la bandeja y marca **Iniciar con Windows**. Se crea una tarea programada que abre Switchify al iniciar sesión.

### Desinstalar

Desmarca **Iniciar con Windows**, elige **Salir** en el menú de la bandeja y borra `Switchify.exe` (o la carpeta `%LOCALAPPDATA%\Programs\Switchify`).

## Limitaciones

Para no depender de programas externos, Switchify no puede convertir **a** WebM, MKV, GIF (vídeo) ni OGG, ni leer archivos OGG/Opus.
