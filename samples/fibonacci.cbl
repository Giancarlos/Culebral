def fibonacci(n: int) -> int:
    if n <= 1:
        return n
    return fibonacci(n - 1) + fibonacci(n - 2)

def main():
    i = 0
    while i < 10:
        result = fibonacci(i)
        print(result)
        i += 1
