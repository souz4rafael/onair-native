"""
Generates all Stream Deck plugin icons as polished, "app-icon"-style rounded-square chips:
diagonal-gradient colored background plate + soft drop shadow + white glyph on top. Replaces
the earlier flat-outline-on-transparent-background version, which read as too plain/toy-like.

Renders at 4x supersampling then downsamples for clean anti-aliased edges at Stream Deck's
required sizes (72/144 for action states, 28/56 for the category icon, 128/256 for marketplace).

Run once at plugin-authoring time; the resulting PNGs are committed to the repo like any other
asset — this script is not needed at build or runtime.
"""
import os
import math
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.join(os.path.dirname(__file__), "com.souz4rafael.onair.sdPlugin", "imgs")
SS = 4  # supersampling factor

WHITE = (255, 255, 255, 255)
NEUTRAL_LIGHT = (120, 128, 138, 255)   # "off"/inactive plate, lighter gradient stop
NEUTRAL_DARK  = (74, 80, 90, 255)      # "off"/inactive plate, darker gradient stop

# Per-action accent colors (lighter, darker) — diagonal gradient stops for the "on"/active plate
ACCENTS = {
    "cyan":   ((34, 211, 238, 255), (8, 145, 178, 255)),     # toggle-tp / open-file
    "amber":  ((251, 191, 36, 255), (217, 119, 6, 255)),     # lock-tp
    "red":    ((248, 113, 113, 255), (220, 38, 38, 255)),    # hide-tp-share / recording
    "purple": ((192, 132, 252, 255), (147, 51, 234, 255)),   # hide-controller-share
    "slate":  ((100, 116, 139, 255), (51, 65, 85, 255)),     # status / release-stealth
    "blue":   ((96, 165, 250, 255), (37, 99, 235, 255)),     # dial-opacity
    "teal":   ((45, 212, 191, 255), (13, 148, 136, 255)),    # dial-font-size
    "orange": ((251, 146, 60, 255), (234, 88, 12, 255)),     # dial-scroll-speed
    "pink":   ((244, 114, 182, 255), (219, 39, 119, 255)),   # dial-voice-scroll-speed
    "violet": ((167, 139, 250, 255), (124, 58, 237, 255)),   # dial-scroll-step
    "gold":   ((250, 204, 21, 255), (202, 138, 4, 255)),     # dial-voice-threshold
    "green":  ((74, 222, 128, 255), (22, 163, 74, 255)),     # scroll-up / scroll-down
}


def canvas(size):
    return Image.new("RGBA", (size, size), (0, 0, 0, 0))


def diagonal_gradient(size, top_left, bottom_right):
    """Linear gradient from top-left to bottom-right, as an RGBA image."""
    base = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    px = base.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1)) if size > 1 else 0
            r = int(top_left[0] + (bottom_right[0] - top_left[0]) * t)
            g = int(top_left[1] + (bottom_right[1] - top_left[1]) * t)
            b = int(top_left[2] + (bottom_right[2] - top_left[2]) * t)
            px[x, y] = (r, g, b, 255)
    return base


def rounded_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def make_plate(size, top_left, bottom_right, shadow=True):
    """Rounded-square gradient-filled plate with an optional soft drop shadow, on a padded
    transparent canvas sized to fit the shadow bleed. Returns an RGBA image of `size`x`size`."""
    pad = int(size * 0.06)
    inner = size - 2 * pad
    radius = int(inner * 0.24)

    plate = diagonal_gradient(inner, top_left, bottom_right)
    mask = rounded_mask(inner, radius)

    # subtle darker border for definition
    border = Image.new("RGBA", (inner, inner), (0, 0, 0, 0))
    bd = ImageDraw.Draw(border)
    darker = tuple(max(0, c - 40) for c in bottom_right[:3]) + (140,)
    bd.rounded_rectangle([0, 0, inner - 1, inner - 1], radius=radius, outline=darker, width=max(1, inner // 60))

    out = canvas(size)
    if shadow:
        shadow_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        shadow_mask = Image.new("L", (size, size), 0)
        shadow_mask.paste(mask, (pad, pad + int(size * 0.035)))
        shadow_fill = Image.new("RGBA", (size, size), (0, 0, 0, 200))
        shadow_layer = Image.composite(shadow_fill, shadow_layer, shadow_mask)
        shadow_layer = shadow_layer.filter(ImageFilter.GaussianBlur(radius=size * 0.045))
        out = Image.alpha_composite(out, shadow_layer)

    plate_rgba = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    plate_rgba.paste(plate, (pad, pad), mask)
    out = Image.alpha_composite(out, plate_rgba)
    border_rgba = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    border_rgba.paste(border, (pad, pad), border)
    out = Image.alpha_composite(out, border_rgba)
    return out, pad, inner


def save_both(img_ss, path_no_ext):
    """Downsample a supersampled (SS*144) image to 144 (@2x) and 72 (@1x)."""
    img144 = img_ss.resize((144, 144), Image.LANCZOS)
    img144.save(path_no_ext + "@2x.png")
    img144.resize((72, 72), Image.LANCZOS).save(path_no_ext + ".png")


# ── Glyph drawers (all draw in WHITE, centered within the plate's inner area) ──────────────

def draw_monitor(draw, cx, cy, s, filled):
    w, h = s * 0.62, s * 0.42
    top = cy - h / 2 - s * 0.05
    rect = [cx - w / 2, top, cx + w / 2, top + h]
    radius = s * 0.05
    lw = max(3, int(s * 0.05))
    if filled:
        draw.rounded_rectangle(rect, radius=radius, fill=WHITE)
    else:
        draw.rounded_rectangle(rect, radius=radius, outline=WHITE, width=lw)
    stand_w, stand_h = w * 0.20, s * 0.07
    stand_top = top + h
    draw.rectangle([cx - stand_w / 2, stand_top, cx + stand_w / 2, stand_top + stand_h], fill=WHITE)
    base_w = w * 0.38
    draw.rounded_rectangle([cx - base_w / 2, stand_top + stand_h, cx + base_w / 2, stand_top + stand_h + s * 0.03],
                            radius=s * 0.015, fill=WHITE)


def draw_padlock(draw, cx, cy, s, locked):
    """Locked/unlocked share the exact same vertical geometry for both the body and the
    shackle's bounding box — only a horizontal `x_offset` parameter differs between the two
    states (0 when locked, shifted when unlocked). This keeps the shackle's bottom edge
    permanently flush with the body's top edge in both states; previously the unlocked state
    shifted the whole shackle bbox diagonally (including vertically), opening up a gap between
    the shackle and the body that didn't exist in the locked state."""
    body_w, body_h = s * 0.42, s * 0.34
    body_top = cy + s * 0.02
    body_left = cx - body_w / 2
    lw = max(3, int(s * 0.06))
    shackle_r = body_w * 0.40
    shackle_bottom = body_top + shackle_r * 0.25   # fixed in both states — the overlap point
    shackle_top = shackle_bottom - shackle_r * 1.8  # fixed in both states — same vertical span
    x_offset = 0 if locked else s * 0.11            # only this changes between states

    bbox = [cx - shackle_r - x_offset, shackle_top, cx + shackle_r - x_offset, shackle_bottom]
    draw.arc(bbox, start=180, end=360, fill=WHITE, width=lw)

    if locked:
        draw.rounded_rectangle([body_left, body_top, body_left + body_w, body_top + body_h],
                                radius=s * 0.045, fill=WHITE)
        kh_r = s * 0.032
        kh_cx, kh_cy = cx, body_top + body_h * 0.42
        draw.ellipse([kh_cx - kh_r, kh_cy - kh_r, kh_cx + kh_r, kh_cy + kh_r], fill=tuple(list(NEUTRAL_DARK[:3]) + [200]))
    else:
        draw.rounded_rectangle([body_left, body_top, body_left + body_w, body_top + body_h],
                                radius=s * 0.045, outline=WHITE, width=lw)


def draw_eye(draw, cx, cy, s, hidden):
    w, h = s * 0.56, s * 0.32
    lw = max(3, int(s * 0.055))
    bbox = [cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2]
    draw.arc(bbox, start=20, end=160, fill=WHITE, width=lw)
    draw.arc(bbox, start=200, end=340, fill=WHITE, width=lw)
    pupil_r = s * 0.08
    if not hidden:
        draw.ellipse([cx - pupil_r, cy - pupil_r, cx + pupil_r, cy + pupil_r], fill=WHITE)
    else:
        pad = s * 0.14
        draw.line([cx - w / 2 + pad * 0.3, cy + h / 2 + pad * 0.3, cx + w / 2 - pad * 0.3, cy - h / 2 - pad * 0.3],
                   fill=WHITE, width=lw)


def draw_control_panel(draw, cx, cy, s, hidden):
    """Distinct glyph for the Controller window (as opposed to hide-tp-share's eye, which
    represents the TP/teleprompter itself): a small settings panel with 3 slider rows. `hidden`
    adds a diagonal slash, mirroring the eye glyph's own crossed-out "hidden" treatment so the
    two actions share a consistent on/off visual language while remaining clearly distinct
    shapes at a glance."""
    w, h = s * 0.5, s * 0.42
    left, top = cx - w / 2, cy - h / 2
    lw = max(3, int(s * 0.05))
    draw.rounded_rectangle([left, top, left + w, top + h], radius=s * 0.05, outline=WHITE, width=lw)
    slider_lw = max(2, int(s * 0.025))
    knob_r = s * 0.035
    for i, frac in enumerate((0.3, 0.52, 0.74)):
        y = top + h * frac
        draw.line([left + w * 0.16, y, left + w * 0.84, y], fill=WHITE, width=slider_lw)
        knob_x = left + w * (0.36 if i != 1 else 0.64)
        draw.ellipse([knob_x - knob_r, y - knob_r, knob_x + knob_r, y + knob_r], fill=WHITE)
    if hidden:
        pad = s * 0.05
        draw.line([left - pad, top + h + pad, left + w + pad, top - pad], fill=WHITE, width=lw + 2)


def draw_record(draw, cx, cy, s, filled):
    r = s * 0.24
    lw = max(3, int(s * 0.06))
    bbox = [cx - r, cy - r, cx + r, cy + r]
    if filled:
        draw.ellipse(bbox, fill=WHITE)
    else:
        draw.ellipse(bbox, outline=WHITE, width=lw)


def draw_status_chip(draw, cx, cy, s):
    w, hh = s * 0.5, s * 0.22
    draw.rounded_rectangle([cx - w / 2, cy - hh / 2, cx + w / 2, cy + hh / 2], radius=s * 0.05,
                            outline=WHITE, width=max(3, int(s * 0.045)))
    for dx in (-0.15, 0, 0.15):
        r = s * 0.04
        x = cx + dx * s
        draw.ellipse([x - r, cy - r, x + r, cy + r], fill=WHITE)


def draw_dial_knob(draw, cx, cy, s):
    r = s * 0.30
    lw = max(3, int(s * 0.065))
    draw.ellipse([cx - r, cy - r, cx + r, cy + r], outline=WHITE, width=lw)
    ang = math.radians(-45)
    x1, y1 = cx + r * 0.3 * math.cos(ang), cy + r * 0.3 * math.sin(ang)
    x2, y2 = cx + r * 0.95 * math.cos(ang), cy + r * 0.95 * math.sin(ang)
    draw.line([x1, y1, x2, y2], fill=WHITE, width=lw)
    dot_r = s * 0.055
    draw.ellipse([cx - dot_r, cy - dot_r, cx + dot_r, cy + dot_r], fill=WHITE)


def draw_eject(draw, cx, cy, s):
    """Release/eject glyph: upward triangle over a bar — universal 'eject' symbol."""
    tri_w, tri_h = s * 0.42, s * 0.30
    top = cy - s * 0.18
    draw.polygon([(cx, top), (cx - tri_w / 2, top + tri_h), (cx + tri_w / 2, top + tri_h)], fill=WHITE)
    bar_w, bar_h = s * 0.46, s * 0.09
    bar_top = top + tri_h + s * 0.08
    draw.rounded_rectangle([cx - bar_w / 2, bar_top, cx + bar_w / 2, bar_top + bar_h], radius=bar_h / 2, fill=WHITE)


def draw_folder(draw, cx, cy, s):
    w, h = s * 0.56, s * 0.38
    top = cy - h / 2 + s * 0.04
    tab_w, tab_h = w * 0.4, h * 0.22
    left = cx - w / 2
    draw.rounded_rectangle([left, top - tab_h, left + tab_w, top], radius=s * 0.025, fill=WHITE)
    draw.rounded_rectangle([left, top, left + w, top + h], radius=s * 0.035, fill=WHITE)


def draw_chevron(draw, cx, cy, s, up):
    w, h = s * 0.4, s * 0.28
    lw = max(4, int(s * 0.075))
    if up:
        pts_l = [(cx - w / 2, cy + h / 2), (cx, cy - h / 2)]
        pts_r = [(cx, cy - h / 2), (cx + w / 2, cy + h / 2)]
    else:
        pts_l = [(cx - w / 2, cy - h / 2), (cx, cy + h / 2)]
        pts_r = [(cx, cy + h / 2), (cx + w / 2, cy - h / 2)]
    draw.line(pts_l, fill=WHITE, width=lw, joint="curve")
    draw.line(pts_r, fill=WHITE, width=lw, joint="curve")


# ── Per-action icon builders ────────────────────────────────────────────────────────────────

def build(name, states):
    """states: dict of state_name -> (accent_key_or_None, glyph_fn). accent_key=None => neutral plate."""
    base = os.path.join(ROOT, "actions", name)
    os.makedirs(base, exist_ok=True)
    for state_name, (accent_key, glyph_fn) in states.items():
        size = 144 * SS
        top_left, bottom_right = ACCENTS[accent_key] if accent_key else (NEUTRAL_LIGHT, NEUTRAL_DARK)
        plate, pad, inner = make_plate(size, top_left, bottom_right)
        draw = ImageDraw.Draw(plate)
        cx = cy = size / 2
        glyph_fn(draw, cx, cy, inner)
        save_both(plate, os.path.join(base, state_name))


def gen_all_actions():
    build("toggle-tp", {
        "closed": (None, lambda d, x, y, s: draw_monitor(d, x, y, s, filled=False)),
        "open":   ("cyan", lambda d, x, y, s: draw_monitor(d, x, y, s, filled=True)),
        "icon":   ("cyan", lambda d, x, y, s: draw_monitor(d, x, y, s, filled=True)),
    })
    build("lock-tp", {
        "unlocked": (None, lambda d, x, y, s: draw_padlock(d, x, y, s, locked=False)),
        "locked":   ("amber", lambda d, x, y, s: draw_padlock(d, x, y, s, locked=True)),
        "icon":     ("amber", lambda d, x, y, s: draw_padlock(d, x, y, s, locked=True)),
    })
    build("hide-tp-share", {
        "visible": (None, lambda d, x, y, s: draw_eye(d, x, y, s, hidden=False)),
        "hidden":  ("red", lambda d, x, y, s: draw_eye(d, x, y, s, hidden=True)),
        "icon":    ("red", lambda d, x, y, s: draw_eye(d, x, y, s, hidden=True)),
    })
    build("hide-controller-share", {
        "visible": (None, lambda d, x, y, s: draw_control_panel(d, x, y, s, hidden=False)),
        "hidden":  ("purple", lambda d, x, y, s: draw_control_panel(d, x, y, s, hidden=True)),
        "icon":    ("purple", lambda d, x, y, s: draw_control_panel(d, x, y, s, hidden=True)),
    })
    build("recording", {
        "idle":      (None, lambda d, x, y, s: draw_record(d, x, y, s, filled=False)),
        "recording": ("red", lambda d, x, y, s: draw_record(d, x, y, s, filled=True)),
        "icon":      ("red", lambda d, x, y, s: draw_record(d, x, y, s, filled=True)),
    })
    build("status", {
        "status": ("slate", draw_status_chip),
        "icon":   ("slate", draw_status_chip),
    })
    build("release-stealth", {
        "icon": ("slate", draw_eject),
    })
    build("open-file", {
        "icon": ("cyan", draw_folder),
    })
    build("scroll-up", {
        "icon": ("green", lambda d, x, y, s: draw_chevron(d, x, y, s, up=True)),
    })
    build("scroll-down", {
        "icon": ("green", lambda d, x, y, s: draw_chevron(d, x, y, s, up=False)),
    })
    build("dial-opacity", {"icon": ("blue", draw_dial_knob), "key": ("blue", draw_dial_knob)})
    build("dial-font-size", {"icon": ("teal", draw_dial_knob), "key": ("teal", draw_dial_knob)})
    build("dial-scroll-speed", {"icon": ("orange", draw_dial_knob), "key": ("orange", draw_dial_knob)})
    build("dial-voice-scroll-speed", {"icon": ("pink", draw_dial_knob), "key": ("pink", draw_dial_knob)})
    build("dial-scroll-step", {"icon": ("violet", draw_dial_knob), "key": ("violet", draw_dial_knob)})
    build("dial-voice-threshold", {"icon": ("gold", draw_dial_knob), "key": ("gold", draw_dial_knob)})


def gen_plugin_icons():
    base = os.path.join(ROOT, "plugin")
    os.makedirs(base, exist_ok=True)

    # Category icon: small + monochrome per Stream Deck sidebar convention (no colored plate).
    size = 56 * SS
    img = canvas(size)
    draw = ImageDraw.Draw(img)
    draw_monitor(draw, size / 2, size / 2, size * 0.82, filled=False)
    img56 = img.resize((56, 56), Image.LANCZOS)
    img56.save(os.path.join(base, "category-icon@2x.png"))
    img56.resize((28, 28), Image.LANCZOS).save(os.path.join(base, "category-icon.png"))

    # Marketplace icon: app's real icon centered on a branded plate, if available.
    app_ico = os.path.join(os.path.dirname(__file__), "..", "OnAirNative", "Assets", "app-icon.ico")
    size = 256 * SS
    top_left, bottom_right = ACCENTS["cyan"]
    plate, pad, inner = make_plate(size, top_left, bottom_right)
    if os.path.exists(app_ico):
        try:
            with Image.open(app_ico) as ico:
                ico = ico.convert("RGBA")
                icon_size = int(inner * 0.62)
                ico_resized = ico.resize((icon_size, icon_size), Image.LANCZOS)
                plate.paste(ico_resized, (int(size / 2 - icon_size / 2), int(size / 2 - icon_size / 2)), ico_resized)
        except Exception as e:
            print("Could not load app-icon.ico, using glyph fallback:", e)
            draw = ImageDraw.Draw(plate)
            draw_monitor(draw, size / 2, size / 2, inner, filled=True)
    else:
        draw = ImageDraw.Draw(plate)
        draw_monitor(draw, size / 2, size / 2, inner, filled=True)

    img256 = plate.resize((256, 256), Image.LANCZOS)
    img256.save(os.path.join(base, "marketplace@2x.png"))
    img256.resize((128, 128), Image.LANCZOS).save(os.path.join(base, "marketplace.png"))


if __name__ == "__main__":
    gen_all_actions()
    gen_plugin_icons()
    print("Icons generated.")
