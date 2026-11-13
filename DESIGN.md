# Design

This file records decisions. It covers the chat pane (`wwwroot/chat.css`); terminals take their colours from the terminal scheme and the list from the app theme.

## Chat pane

A chat is prose, code and decisions to read, so it has a surface of its own instead of the terminal's black: a neutral dark grey page with off-white text. The reference is a code host's dark reading view, not a terminal and not another vendor's chat app. One accent, blue, is kept for links, focus and the one primary action.

Every colour is a token defined at the top of `chat.css`, in a dark set and a light set (`data-scheme`). Use the token for its role only; no hex anywhere else.

| Token | Dark | Light | Role |
| --- | --- | --- | --- |
| `--chat-bg` | `#1c1e22` | `#fbfbfc` | The page |
| `--chat-raised` | `#272a30` | `#f0f1f4` | What sits on the page: your message, a code or output block, a decision card, the message box |
| `--chat-over` | `#33373e` | `#ffffff` | What floats over the page: menus |
| `--fg` | `#dcdfe4` | `#1f2328` | Text |
| `--fg2` | `#adb2bb` | `#4a505a` | Secondary text, labels |
| `--fg3` | `#969ca6` | `#666c77` | Muted text on the page: placeholders, states that need no action |
| `--pr-line` | `#353941` | `#dcdfe4` | Separators |
| `--pr-line-strong` | `#7a808a` | `#7f8590` | The edge of a control or of a card that waits for an answer |
| `--pr-blue` / `--chat-onaccent` | `#7fadf8` / `#10141b` | `#1f5fd1` / `#ffffff` | Accent and the text on it |
| `--pr-red`, `--pr-amber`, `--pr-green` | `#f08a8c`, `#e2b55a`, `#72c784` | `#b3261e`, `#8a5a00`, `#1f7a35` | Failed or removed, needs attention, added |
| `--chat-tint`, `--chat-tint-strong` | text at 9% and 16% | same | Inline code, a button's fill, hover and selection on any surface |

Rules that follow from the roles:

- Text is at least 4.5:1 on every surface it appears on, and a control's edge at least 3:1. `python tools/chat-contrast.py` checks the pairs; run it when a token changes.
- A block is one step lighter than what it sits on, never darker: `--chat-raised` on the page, `--chat-tint` inside something raised (`--chat-well` carries this).
- A border means "this is a control" or "this waits for you". Your own messages and code blocks have a fill and no border; a plan or a permission request has both.
- Only a decision is a card. A tool call is a line that opens.
- Radii: 14 px for a message and a card, 16 px for the message box, 10 px for a block, 8 px for a button and anything inside the message box, 6 px for a row in a menu.
- Type: Segoe UI Variable Text at the terminal's size setting (16 px at the default), 1.65 line height; code in the terminal's font at 0.875em. Prose lines stop at 74 characters unless the reader has dragged the column wider. Where there is no Segoe (a phone, a Mac) the face is the system's own, never the browser's default sans.
- States: hover lightens a fill (`--chat-tint-strong`), focus is a 2 px accent outline, a pending message has a dashed edge, a control that no longer applies is removed or disabled with `--fg3` text.

### On a phone

The phone shows the same pane (`.pane.chat.touch`), with these differences, each because of the hand and the screen:

- Type is the system face at 17 px with 1.5 line height, the size iOS sets its own body text in. Nothing typed into or pressed is below 16 px.
- The message box keeps its plain edge while typing. On a desktop the accent edge says which of several panes has the keyboard; a phone has one pane and the keyboard itself says it.
- What is pressed in the message box is at least 36 px tall and has a fill (`--chat-tint`), so it reads as pressable without an edge: attach, the three pickers, send (40 px). They sit in one row that never wraps; a picker shortens with an ellipsis before the row breaks.
- A model an agent lists as `provider/Name` shows as the name alone, with the provider as a heading in the list (this one applies on the desktop too).
- The app shows no bar over the keyboard (WebKit's previous/next/done). A tap on the conversation puts the keyboard away.
