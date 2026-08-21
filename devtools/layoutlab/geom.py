"""Revit-free 2D primitives — a faithful port of Core/Vec2.cs, Box2.cs, Seg2.cs.

Every distance is in model feet. Paper-space values reaching the layout core have
already been multiplied by the view scale, exactly as in the C# engine.
"""
import math


class Vec2:
    __slots__ = ("x", "y")

    def __init__(self, x=0.0, y=0.0):
        self.x = float(x)
        self.y = float(y)

    def __add__(self, o):  return Vec2(self.x + o.x, self.y + o.y)
    def __sub__(self, o):  return Vec2(self.x - o.x, self.y - o.y)
    def __mul__(self, s):  return Vec2(self.x * s, self.y * s)
    __rmul__ = __mul__

    def dot(self, o):      return self.x * o.x + self.y * o.y

    @property
    def length(self):      return math.hypot(self.x, self.y)

    def normalized(self):
        n = self.length
        return Vec2(0, 0) if n < 1e-9 else Vec2(self.x / n, self.y / n)

    def perp(self):        return Vec2(-self.y, self.x)

    def __repr__(self):    return f"({self.x:.4f},{self.y:.4f})"


class Box2:
    """Axis-aligned bounding box. Port of Core/Box2.cs."""
    __slots__ = ("min_x", "min_y", "max_x", "max_y")

    def __init__(self, min_x, min_y, max_x, max_y):
        self.min_x = min(min_x, max_x)
        self.min_y = min(min_y, max_y)
        self.max_x = max(min_x, max_x)
        self.max_y = max(min_y, max_y)

    @property
    def width(self):   return self.max_x - self.min_x
    @property
    def height(self):  return self.max_y - self.min_y
    @property
    def area(self):    return self.width * self.height

    def expand(self, pad):
        return Box2(self.min_x - pad, self.min_y - pad, self.max_x + pad, self.max_y + pad)

    def chebyshev_gap(self, o):
        gx = max(0.0, max(o.min_x - self.max_x, self.min_x - o.max_x))
        gy = max(0.0, max(o.min_y - self.max_y, self.min_y - o.max_y))
        return max(gx, gy)

    @staticmethod
    def from_center(c, half_w, half_h):
        return Box2(c.x - half_w, c.y - half_h, c.x + half_w, c.y + half_h)

    @staticmethod
    def from_points(a, b):
        return Box2(a.x, a.y, b.x, b.y)

    def intersects(self, o):
        return (self.min_x < o.max_x and self.max_x > o.min_x
                and self.min_y < o.max_y and self.max_y > o.min_y)

    def overlap_area(self, o):
        ox = min(self.max_x, o.max_x) - max(self.min_x, o.min_x)
        oy = min(self.max_y, o.max_y) - max(self.min_y, o.min_y)
        return 0.0 if (ox <= 0 or oy <= 0) else ox * oy

    def contains_box(self, o):
        return (o.min_x >= self.min_x and o.max_x <= self.max_x
                and o.min_y >= self.min_y and o.max_y <= self.max_y)

    def contains(self, p):
        return self.min_x <= p.x <= self.max_x and self.min_y <= p.y <= self.max_y

    def union(self, o):
        return Box2(min(self.min_x, o.min_x), min(self.min_y, o.min_y),
                    max(self.max_x, o.max_x), max(self.max_y, o.max_y))

    def __repr__(self):
        return f"[{self.min_x:.2f},{self.min_y:.2f} -> {self.max_x:.2f},{self.max_y:.2f}]"


class Seg2:
    """2D segment with the C# proper-crossing test (shared endpoints do NOT count)."""
    __slots__ = ("a", "b")

    def __init__(self, a, b):
        self.a = a
        self.b = b

    @property
    def length(self):  return (self.b - self.a).length
    @property
    def bounds(self):  return Box2.from_points(self.a, self.b)

    def crosses(self, o):
        eps = 1e-9
        d1 = _cross(self.b - self.a, o.a - self.a)
        d2 = _cross(self.b - self.a, o.b - self.a)
        d3 = _cross(o.b - o.a, self.a - o.a)
        d4 = _cross(o.b - o.a, self.b - o.a)
        s1 = (d1 > eps and d2 < -eps) or (d1 < -eps and d2 > eps)
        s2 = (d3 > eps and d4 < -eps) or (d3 < -eps and d4 > eps)
        return s1 and s2

    def __repr__(self):  return f"{self.a} -> {self.b}"


def _cross(u, v):
    return u.x * v.y - u.y * v.x
