"""Port of Core/DimensionPlan.cs — the plan objects the layout mutates."""
from geom import Vec2, Box2

# SegmentTextState
INLINE     = "Inline"
FLIPPED    = "Flipped"
STAGGERED  = "Staggered"
LEADER_OUT = "LeaderOut"

# DimSide
POSITIVE = "Positive"
NEGATIVE = "Negative"


class PlannedSegment:
    __slots__ = ("length_ft", "text_width_ft", "text_state", "tag_pos", "value_str")

    def __init__(self, length_ft=0.0, text_width_ft=0.0, value_str=""):
        self.length_ft = length_ft
        self.text_width_ft = text_width_ft
        self.text_state = INLINE
        self.tag_pos = None
        self.value_str = value_str

    @property
    def is_cramped(self):
        return self.text_width_ft > self.length_ft


class PlannedDimension:
    def __init__(self):
        self.source_key = ""
        self.target_key = ""
        self.target_type = "Grid"
        self.source_point = Vec2()
        self.target_point = Vec2()
        self.axis_dir = Vec2(1, 0)
        self.side = POSITIVE
        self.tag_column_dir = 1
        self.cluster_id = ""
        self.region = None          # Box2 or None (None == HasRegion false)
        self.offset_ft = 0.0
        self.segments = []
        self.ref_anchors = []
        self.paper_bounds = Box2(0, 0, 0, 0)
        self.anatomy = None

    @property
    def has_region(self):
        return self.region is not None
