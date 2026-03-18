interface Describable:
    def describe() -> str

class Dog(Describable):
    name: str = ""

    def __init__(name: str):
        @name = name

    def describe() -> str:
        return f"Dog named {@name}"

class Cat(Describable):
    name: str = ""

    def __init__(name: str):
        @name = name

    def describe() -> str:
        return f"Cat named {@name}"

def main():
    d = Dog("Rex")
    print(d.describe())
    c = Cat("Whiskers")
    print(c.describe())
