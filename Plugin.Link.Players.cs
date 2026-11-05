using System;
using System.Collections.Generic;
using System.Text.Json;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Maestro Link — the PLAYERS panel (approved design 2026-11-05): the room's roster from the server's `players`
// message, one raised tile per player (own row first), with a live state chip. The row mapping itself is the pure
// LinkPlayerMap.Map (LinkPlayers.cs); this file only holds the received list and draws it.
//
// The list survives a reconnect (the last roster stays up while "Reconnecting…") and is cleared on EndLink.
public sealed partial class Plugin
{
    // Pre-built row slots (ListElement shows the first N). The room caps players server-side (maxPlayers, 10 today);
    // extra slots are headroom — a roster longer than this just shows its first PlayerSlots rows.
    private const int PlayerSlots = 16;

    // Column widths (fixed, so header and rows line up; the instrument column takes the rest). Row = 540 window − 2×12
    // body − 2×10 panel − 9 scroll-bar inset − 2×6 tile = 475 inner: 3 + 10 + 130 + 72 + 44 + 5 gaps × 6 = 289 fixed
    // → ~186 instrument.
    private const float PlayerBarW  = 3f;
    private const float PlayerDotW  = 10f;
    private const float PlayerNameW = 130f;
    private const float PlayerPingW = 44f;
    private const float PlayerColGap = 6f;
    private const float PlayerTilePadX = 6f;
    private const float PlayerTilePadY = 3f;

    private readonly List<LinkPlayer> _lkPlayers = new();   // own row first, then server (join) order
    private readonly List<LinkPlayerRow> _lkPlayerRows = new();
    private int  _lkMaxPlayers;
    private bool _lkPlayersLoaded;   // false until the first `players` message (→ "Loading players…")
    private bool _lkPlayersShown = true;

    private void InitLinkPlayers() => _lkPlayersShown = _cfg.Get<bool>("link_players_shown", true);

    private void OnLinkPlayers(JsonElement m)
    {
        var (max, list) = LinkPlayer.ParseMessage(m);
        _lkPlayers.Clear();
        _lkPlayers.AddRange(LinkPlayerMap.OwnFirst(list, _lkPlayerId));
        RemapLinkPlayers(LinkSongRunning());
        _lkMaxPlayers = max;
        _lkPlayersLoaded = true;
    }

    // "A song is running in the room" for the Problem rule (LinkPlayerMap.Map): the room song has started and is not
    // paused — a prepared / next song does not count. It changes without a `players` message (song start, pause, end),
    // so the cached rows are re-mapped whenever it flips (PlayerRows, polled by the slot Funcs).
    private bool _lkRowsRunning;

    private bool LinkSongRunning() => _lkRoomSong != null && !_lkPaused;

    private List<LinkPlayerRow> PlayerRows()
    {
        bool running = LinkSongRunning();
        if (running != _lkRowsRunning) RemapLinkPlayers(running);
        return _lkPlayerRows;
    }

    private void RemapLinkPlayers(bool songRunning)
    {
        _lkRowsRunning = songRunning;
        _lkPlayerRows.Clear();
        foreach (var p in _lkPlayers) _lkPlayerRows.Add(LinkPlayerMap.Map(p, songRunning));
    }

    private void ClearLinkPlayers()
    {
        _lkPlayers.Clear();
        _lkPlayerRows.Clear();
        _lkMaxPlayers = 0;
        _lkPlayersLoaded = false;
    }

    private void ToggleLinkPlayersShown()
    {
        _lkPlayersShown = !_lkPlayersShown;
        _cfg.Set<bool>("link_players_shown", _lkPlayersShown); _cfg.Save();
    }

    // "3 / 10 · 2 playing" (count = everyone on the roster, offline included).
    private string LinkPlayersHint()
    {
        if (!_lkPlayersLoaded) return "";
        int playing = 0;
        foreach (var r in PlayerRows()) if (r.Chip == LinkChip.Playing) playing++;
        int max = _lkMaxPlayers > 0 ? _lkMaxPlayers : _lkPlayers.Count;
        return _loc.TFormat("mst.link.players.count", _lkPlayers.Count, max, playing);
    }

    // The row list RESERVES exactly PlayerRowsVisible rows at all times (so the window never changes height as people
    // join / leave — user decision 2026-11-06) and scrolls past that: a FixedHeight ScrollElement (SDK ≥ 2.26; the hub
    // pattern of Stellar-Design-Language.md). Height computed from the row build below, not measured:
    //   tile  = chip 24 (ChipStrutHeight 22 + ChipPill PaddingY 1×2; the tallest cell) + PlayerTile PaddingY 3×2 = 30
    //   pitch = the ListElement's own fixed 2 px slot spacing (WindowBuilder.Layout BuildList) — not RowGap
    //   5 rows = 5×30 + 4×2 = 158.
    // Unused slots are SetActive(false) by the ListElement → ignored by the content VLG (no blank space inside the
    // scroll). The header stays OUTSIDE the scroll; "Loading players…" sits inside the reserved area.
    private const int   PlayerRowsVisible = 5;
    private const float PlayerRowHeight   = ChipStrutHeight + 2f * 1f + 2f * PlayerTilePadY;   // 30
    private const float PlayerListGap     = 2f;
    private const float PlayerListHeight  = PlayerRowsVisible * PlayerRowHeight + (PlayerRowsVisible - 1) * PlayerListGap;
    // The scroll viewport is ALWAYS inset 9 px from the right for its bar (5 px bar + 4 px gap, WindowBuilder.Widgets
    // BuildScroll), even when the bar auto-hides — so the header outside the scroll gets the same 9 px on its right or
    // the State / Ping columns would sit 9 px right of the row columns.
    private const float ScrollBarInset    = 9f;

    private HudElement BuildLinkPlayersPanel() => SectionPanelWithTrailing(
        () => _loc.T("mst.link.cap.players"), LinkPlayersHint, PlayersToggle(),
        new ConditionalElement(() => _lkPlayersShown, new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new CellElement(PlayerHeaderRow(), Weight: 1f),
                new SpacerElement(ScrollBarInset),
            }, Gap: 0.01f),
            new ScrollElement(new ConditionalElement(() => !_lkPlayersLoaded,
                    new TextElement(() => _loc.T("mst.link.players.loading"), Color: () => MutedColor()),
                    new ListElement(() => Math.Min(_lkPlayers.Count, PlayerSlots), BuildPlayerSlots())),
                Height: PlayerListHeight) { FixedHeight = true },
        }, Gap: 4f)));


    // Small Hide/Show text toggle at the right of the caption row: muted bold 12 (same size as the caption, so the
    // collapsed panel is just the caption row) with a soft hover wash. Bare selectable = no tint/padding of its own.
    private bool _lkPlayersToggleHover;

    private HudElement PlayersToggle() => new BubbleElement(new SelectableElement(
            new TextElement(() => _loc.T(_lkPlayersShown ? "mst.link.players.hide" : "mst.link.players.show"),
                Color: () => _lkPlayersToggleHover ? TextColor() : MutedColor(), NoWrap: true, FontSize: 12) { Bold = true },
            OnClick: ToggleLinkPlayersShown)
        { Bare = true, OnHover = over => _lkPlayersToggleHover = over })
    {
        FillColorFn = () => _lkPlayersToggleHover ? TextColor() with { A = 0.08f } : Transparent,
        CornerRadius = 4f, PaddingX = 4f, PaddingY = 0f,
    };

    // Header: muted bold, on a transparent tile with the rows' padding so the columns sit over the row columns.
    private HudElement PlayerHeaderRow() => PlayerTile(new RowElement(new HudElement[]
    {
        new CellElement(new SpacerElement(0f), Width: PlayerBarW),
        new CellElement(new SpacerElement(0f), Width: PlayerDotW),
        new CellElement(PlayerHeaderText("mst.link.col.name"), Width: PlayerNameW),
        new CellElement(PlayerHeaderText("mst.link.col.instrument"), Weight: 1f),
        new CellElement(PlayerHeaderText("mst.link.col.state", TextAlign.Center), Width: ChipWidth),
        new CellElement(PlayerHeaderText("mst.link.col.ping", TextAlign.Right), Width: PlayerPingW),
    }, Gap: PlayerColGap), () => Transparent);

    private TextElement PlayerHeaderText(string key, TextAlign align = TextAlign.Left) =>
        new(() => _loc.T(key), Color: () => MutedColor(), Align: align, NoWrap: true, FontSize: 12) { Bold = true };

    // A raised row tile (live TileColor), ~30 px: chip 24 + 2×3 padding.
    private static HudElement PlayerTile(HudElement row, Func<ColorRgba> fill) =>
        new BubbleElement(row) { FillColorFn = () => fill(), PaddingX = PlayerTilePadX, PaddingY = PlayerTilePadY };

    private HudElement[] BuildPlayerSlots()
    {
        var slots = new HudElement[PlayerSlots];
        for (int i = 0; i < PlayerSlots; i++) slots[i] = BuildPlayerSlot(i);
        return slots;
    }

    // Every Func re-resolves its player by slot index, so a roster update just repaints the slots.
    private HudElement BuildPlayerSlot(int i)
    {
        LinkPlayer? P() => i < _lkPlayers.Count ? _lkPlayers[i] : null;
        LinkPlayerRow? R() => PlayerRows() is var rows && i < rows.Count ? rows[i] : null;
        bool Mine() => P() is { } p && _lkPlayerId.Length > 0 && p.PlayerId == _lkPlayerId;

        return PlayerTile(new RowElement(new HudElement[]
        {
            // Own row: a 3 px accent bar at the left edge (transparent on other rows, so the columns never shift).
            new CellElement(new BubbleElement(new SpacerElement(0f, ChipStrutHeight))
                { FillColorFn = () => Mine() ? AccentColor() : Transparent, PaddingX = 0f, PaddingY = 0f, CornerRadius = 1f },
                Width: PlayerBarW),
            new CellElement(new TextElement(() => "●", Color: () => PlayerDotColor(R()), NoWrap: true), Width: PlayerDotW),
            // Bold can't toggle live → own (bold) and other (regular) name are two complete texts in the same cell.
            new CellElement(new ConditionalElement(Mine,
                new TextElement(() => P() is { } p ? $"{p.Name} {_loc.T("mst.link.players.you")}" : "",
                    Color: () => TextColor(), NoWrap: true) { Bold = true, Ellipsize = true },
                new TextElement(() => P()?.Name ?? "",
                    Color: () => R() is { NameMuted: true } ? MutedColor() : TextColor(), NoWrap: true) { Ellipsize = true }),
                Width: PlayerNameW),
            new CellElement(new TextElement(() => PlayerInstText(P(), R()), Color: () => PlayerInstColor(R()), NoWrap: true)
                { Ellipsize = true }, Weight: 1f),
            new CellElement(StatusChip(() => R() is { } r ? _loc.T(ChipKey(r.Chip)) : "", () => R() is { } r ? ChipColor(r.Chip) : null),
                Width: ChipWidth),
            new CellElement(new TextElement(() => PlayerPingText(P(), R()), Color: () => MutedColor(), Align: TextAlign.Right, NoWrap: true),
                Width: PlayerPingW),
        }, Gap: PlayerColGap), TileColor);
    }

    private ColorRgba PlayerDotColor(LinkPlayerRow? r) => r?.Dot switch
    {
        LinkDot.Online => StatusGreen,
        LinkDot.Away   => StatusAmber,
        _              => MutedColor(),
    };

    private string PlayerInstText(LinkPlayer? p, LinkPlayerRow? r)
    {
        if (p == null || r == null) return "";
        switch (r.Value.Inst)
        {
            case LinkInstCol.Problem: return _loc.T(ProblemKey(r.Value.Problem));
            case LinkInstCol.NoPart:       return _loc.T("mst.link.players.noPart");
            case LinkInstCol.NoInstrument: return _loc.T("mst.link.players.noInstrument");
        }
        if (p.InstrumentId is not int inst || inst <= 0) return "—";   // unreachable: the mapping says NoInstrument
        // Tone unknown → instrument only (never a guessed tone); 0 = the instrument's default tone (LinkEquipName).
        return p.ToneId is int tone ? LinkEquipName(inst, tone) : LinkInstrumentName(inst);
    }

    private ColorRgba PlayerInstColor(LinkPlayerRow? r) => r switch
    {
        { Inst: LinkInstCol.Problem } => ProblemText,
        { Inst: LinkInstCol.NoPart or LinkInstCol.NoInstrument } or { NameMuted: true } => MutedColor(),
        _ => TextColor(),
    };

    private string PlayerPingText(LinkPlayer? p, LinkPlayerRow? r)
    {
        if (p == null || r == null) return "";
        return r.Value.ShowPing && p.PingMs is int ms && ms >= 0 ? _loc.TFormat("mst.link.ping", ms) : "—";
    }

    private static string ProblemKey(string? warning) => warning switch
    {
        "band_view_closed" => "mst.link.problem.bandView",
        "no_instrument"    => "mst.link.problem.noInstrument",
        "not_buffered"     => "mst.link.problem.notBuffered",
        _                  => "mst.link.problem.notSynced",
    };

    private static string ChipKey(LinkChip c) => c switch
    {
        LinkChip.Playing   => "mst.link.chip.playing",
        LinkChip.Ready     => "mst.link.chip.ready",
        LinkChip.Switch    => "mst.link.chip.switch",
        LinkChip.Stopped   => "mst.link.chip.stopped",
        LinkChip.Resting   => "mst.link.chip.resting",
        LinkChip.Problem   => "mst.link.chip.problem",
        LinkChip.Reconnect => "mst.link.chip.reconnect",
        LinkChip.Offline   => "mst.link.chip.offline",
        _                  => "mst.link.chip.idle",
    };

    // Fixed semantic colours; null = the muted chip variant (Resting / Idle / Offline).
    private static ColorRgba? ChipColor(LinkChip c) => c switch
    {
        LinkChip.Playing or LinkChip.Ready => StatusGreen,
        LinkChip.Switch or LinkChip.Stopped or LinkChip.Reconnect => StatusAmber,
        LinkChip.Problem => StatusRed,
        _ => null,
    };
}
