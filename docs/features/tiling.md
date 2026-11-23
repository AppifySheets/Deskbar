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
- Drag the separation between two tiles to resize them.

The layout you make is kept, across restarts too.

To tidy up, the row's menu and the tile button's right-click menu offer **Even out tiles** while a split is off centre. Every split goes back to equal and your shape stays. After that the item reads **Reset to grid**, which forgets the shape and goes back to the automatic grid.

A new window goes into a space kept open on any screen, or onto a screen with nothing of the workspace on it, before it takes half of a tile. A window you drop on a screen stays on that screen. To keep a tile from ever being halved by a new window, right-click its icon on the bar and tick **Don't split this tile**, or click the padlock on the tile itself. The lock lasts until the window closes or Deskbar restarts.

The padlock sits in each tile's title bar, just left of the minimize button. A locked tile always shows it, closed on a blue square. An unlocked tile shows an open padlock only while the mouse is over its title bar. Clicking it locks or unlocks the tile without switching to that window. While a tile is moving, its padlock steps out of the way and comes back when the tile settles. In VS Code it covers VS Code's own "always on top" button.

## Settings

The gap between tiled windows is in the Windows on the desktop section of Manage's Settings tab. It takes effect when you press Apply.

## From a script

`deskbar.cmd lock-tile <hwnd> on` does what **Don't split this tile** does. The window handle comes from `deskbar.cmd windows`.

## Related

- [Where windows go](where-windows-go.md) covers which desktop a window lands on.
- [The bar](the-bar.md) has the tile button and the row menu.
- [Manage](manage.md) has the gap setting.
