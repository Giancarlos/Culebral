class Counter:
    count: int = 0

    def __init__(initial: int = 0):
        count = initial

    def increment() -> int:
        count += 1
        return count

    def reset():
        count = 0

struct Point:
    x: float
    y: float

    def distance_to(other: Point) -> float:
        return ((x - other.x) ** 2 + (y - other.y) ** 2) ** 0.5

enum Shape:
    Circle(radius: float)
    Rectangle(width: float, height: float)
    Point

def main():
    c = Counter(0)
    print(c.increment())
    print(c.increment())
