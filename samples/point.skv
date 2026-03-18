class Point:
    x: int = 0
    y: int = 0

    def __init__(x: int, y: int):
        @x = x
        @y = y

    def manhattan_distance() -> int:
        if @x < 0:
            ax = 0 - @x
        else:
            ax = @x
        if @y < 0:
            ay = 0 - @y
        else:
            ay = @y
        return ax + ay

    def to_string() -> str:
        return f"({@x}, {@y})"

def main():
    p = Point(3, 4)
    print(p.to_string())
    print(p.manhattan_distance())
    q = Point(-5, 7)
    print(q.to_string())
    print(q.manhattan_distance())
