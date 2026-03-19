def repeat(s: str, n: int) -> str:
    result = ""
    i = 0
    while i < n:
        result = result + s
        i += 1
    return result

def main():
    print(repeat("ha", 3))
    print(repeat("abc", 2))
