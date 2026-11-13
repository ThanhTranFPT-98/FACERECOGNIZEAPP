"""Boxes and polygons, all in fractions of the frame (0-1), the same units the station stores and the message carries."""

Box = tuple[float, float, float, float]          # x, y, w, h
Point = tuple[float, float]


def clamp01(value: float) -> float:
    return 0.0 if value < 0 else 1.0 if value > 1 else value


def normalise(box: Box) -> Box:
    """Clip a box to the frame, keeping its far edges inside."""
    x, y, w, h = box
    x1, y1 = clamp01(x), clamp01(y)
    x2, y2 = clamp01(x + w), clamp01(y + h)
    return (x1, y1, max(x2 - x1, 0.0), max(y2 - y1, 0.0))


def iou(a: Box, b: Box) -> float:
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    left, top = max(ax, bx), max(ay, by)
    right, bottom = min(ax + aw, bx + bw), min(ay + ah, by + bh)
    if right <= left or bottom <= top:
        return 0.0
    overlap = (right - left) * (bottom - top)
    union = aw * ah + bw * bh - overlap
    return overlap / union if union > 0 else 0.0


def covered(inner: Box, outer: Box) -> float:
    """How much of `inner` lies inside `outer` (0-1). Used to attach a PPE box to a person."""
    ix, iy, iw, ih = inner
    ox, oy, ow, oh = outer
    left, top = max(ix, ox), max(iy, oy)
    right, bottom = min(ix + iw, ox + ow), min(iy + ih, oy + oh)
    if right <= left or bottom <= top or iw <= 0 or ih <= 0:
        return 0.0
    return ((right - left) * (bottom - top)) / (iw * ih)


def centre(box: Box) -> Point:
    x, y, w, h = box
    return (x + w / 2, y + h / 2)


def anchor(box: Box) -> Point:
    """The point that decides which ROI a person is in: the middle of their feet, where they stand."""
    x, y, w, h = box
    return (x + w / 2, min(y + h, 1.0))


def in_polygon(point: Point, polygon: list[Point]) -> bool:
    """Ray casting. A polygon of fewer than 3 points covers nothing; the caller treats that as "whole frame"."""
    if len(polygon) < 3:
        return False
    px, py = point
    inside = False
    previous = polygon[-1]
    for current in polygon:
        x1, y1 = previous
        x2, y2 = current
        # The edge must straddle the horizontal ray; the half-open test keeps a vertex from counting twice.
        if (y1 > py) != (y2 > py):
            crossing = x1 + (py - y1) * (x2 - x1) / (y2 - y1)
            if crossing > px:
                inside = not inside
        previous = current
    return inside
