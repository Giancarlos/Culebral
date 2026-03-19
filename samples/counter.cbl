class Counter:
    count: int = 0

    def __init__(initial: int):
        count = initial

    def increment() -> int:
        count += 1
        return count

    def get_count() -> int:
        return count

def main():
    c = Counter(10)
    print(c.increment())
    print(c.increment())
    print(c.get_count())
