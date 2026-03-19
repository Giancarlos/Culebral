def square(x: int) -> int:
    return x * x

def double(x: int) -> int:
    return x + x

def main():
    print(square(double(3)))
    print(double(square(4)))
