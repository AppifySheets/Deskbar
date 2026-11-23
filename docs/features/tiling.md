---
title: Tiling
summary: Tile windows lays out a workspace's windows on each screen, starting from a grid, and keeps the layout you make.
order: 5
clip: tiling-drop
---

**Tile windows** lays a workspace's windows out on each screen while you are on it, starting from a grid, and keeps whatever layout you make from there.

## How to use it

Turn it on with the tile button at the top of the bar, beside Search, or from the workspace row's menu. Left to itself, Deskbar never changes a window's size or where it sits on screen. Tiling is for a workspace where you want it, and you turn it on.

Rearrange the tiles by dragging:

- Drop a tile on the edge of another to put it above, below or beside that one. It takes half of that tile's room and leaves the rest alone, so two tiles can sit side by side over a third that spans the screen.
- Drop a tile close to the screen's own edge and it gets that whole side.
- Drop a tile on the middle of another to swap the two.
- Drag the separation between two tiles to resize them. Where two separations cross, the drag moves only the part beside the tile you hold, so in four tiles in a square, widening the top left one leaves the bottom two as they are. Once the separations no longer line up, a separation that ends against another one moves whole.

The layout you make is kept, across restarts too.

To tidy up, the row's menu and the tile button's right-click menu offer **Even out tiles** while a split is off centre. Every split goes back to equal and your shape stays. After that the item reads **Reset to grid**, which forgets the shape and goes back to the automatic grid.

A new window goes into a space kept open on any screen, or onto a screen with nothing of the workspace on it, before it takes half of a tile. A window you drop on a screen stays on that screen. To keep a tile from ever being halved by a new window, right-click its icon on the bar and tick **Don't split this tile**. The lock lasts until the window closes or Deskbar restarts.

To keep the windows you work in from shrinking, right-click one of their icons and tick **Split <app> windows last** (for VS Code, **Split Code windows last**). A new window then still lands beside the window you were using, unless that window is one of the app's three you used most recently: then the largest other tile gives it room, on this screen or another. Those three are halved only when no other tile is left, the one used longest ago first. The setting covers every window of the app and is kept across restarts.

## Settings

The gap between tiled windows is in the Windows on the desktop section of Manage's Settings tab. It takes effect when you press Apply.

## From a script

`deskbar.cmd split-last <hwnd> on` does what **Split <app> windows last** does, for the app owning that window. `deskbar.cmd lock-tile <hwnd> on` does what **Don't split this tile** does. The window handle comes from `deskbar.cmd windows`.

## Related

- [Where windows go](where-windows-go.md) covers which desktop a window lands on.
- [The bar](the-bar.md) has the tile button and the row menu.
- [Manage](manage.md) has the gap setting.
