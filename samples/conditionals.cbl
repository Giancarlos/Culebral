def classify(n: int) -> str:
    if n > 0:
        return "positive"
    elif n < 0:
        return "negative"
    else:
        return "zero"

def main():
    print(classify(42))
    print(classify(-7))
    print(classify(0))
