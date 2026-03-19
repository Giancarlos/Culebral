from System.IO import File, Path
from System import Math, Environment
from System.Text import StringBuilder

def main():
    # File I/O via System.IO
    temp_dir = Path.get_temp_path()
    file_path = Path.combine(temp_dir, "culebral_demo.txt")
    File.write_all_text(file_path, "Hello from Culebral .NET interop!")
    content = File.read_all_text(file_path)
    print(content)

    # Math operations
    print(f"max(42, 17) = {Math.max(42, 17)}")
    print(f"min(42, 17) = {Math.min(42, 17)}")
    print(f"abs(-99) = {Math.abs(-99)}")

    # Environment
    home = Environment.get_environment_variable("HOME")
    print(f"HOME = {home}")

    # String methods (case-bridged automatically)
    greeting = "hello, world!"
    print(greeting.to_upper())
    print(greeting.replace("world", "culebral"))
    has_world = greeting.contains("world")
    print(f"contains world: {has_world}")

    # StringBuilder
    sb = StringBuilder()
    sb.append("Built ")
    sb.append("with ")
    sb.append("StringBuilder!")
    print(sb.to_string())

    # Cleanup
    File.delete(file_path)
    print("Done!")
