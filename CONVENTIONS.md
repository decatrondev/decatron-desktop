# Convenciones

Mismas reglas que Flowdeck (`StreamDeckPlatform/CONVENTIONS.md`), resumidas:

## Estructura

- **Un módulo nunca referencia a otro módulo.** Lo compartido pasa por `Decatron.Desktop.Sdk`
  (contratos) o `Decatron.Desktop.Core` (implementaciones: conexión, ajustes, audio).
- El shell (`Decatron.Desktop`) es el único que conoce la lista de módulos (`Services/AppHost.cs`).
  Agregar un módulo = un proyecto `Decatron.Desktop.Modules.X` + una línea ahí.
- **Un solo WebSocket al backend** (`/api/desktop/ws`), multiplexado por canal. Ningún módulo abre
  conexiones propias al servidor; si necesita algo nuevo, se agrega un canal del lado servidor
  (`IDesktopChannel` en el repo del bot) y se habla por `IDesktopConnection`.
- El `Id` del módulo coincide con el nombre de su canal en el servidor.
- Cada módulo guarda sus ajustes vía `IModuleSettings`; nada de archivos propios.
- Nada de diálogos ni chrome nativo del sistema: la ventana, la barra de título y los avisos son
  nuestros. El instalador y las actualizaciones son Velopack, sin wizard.

## C#

- `namespace` de una línea, nulabilidad explícita, comentarios solo para el *por qué*.
- Los eventos de red y audio llegan en hilos ajenos: toda mutación de estado observable pasa
  por `Dispatcher.UIThread`.
- Nada que dependa de la red real en tests: `FakeDesktopServer` habla el mismo protocolo que
  el servidor y se levanta en un puerto libre.

## Commits

- En español, explicando el *por qué*. Un commit por unidad coherente.
