/*
 * Air Motion – C++ (cross‑platform)
 * Generates X, Y, Z acceleration data (m/s²) simulating handheld motion.
 * Compile: g++ -std=c++17 -o airmotion airmotion.cpp
 */
#include <iostream>
#include <random>
#include <thread>
#include <chrono>
#include <cmath>

class AirMotion {
public:
    struct Vector3 {
        double x, y, z;
    };

    AirMotion() : gen(rd()), noise(-0.5, 0.5) {}

    // Simulate one sample of motion (with gravity on Z)
    Vector3 sample() {
        double t = std::chrono::steady_clock::now().time_since_epoch().count() * 1e-9;
        // Gentle swaying + noise
        double ax = 0.3 * std::sin(t * 0.8) + noise(gen);
        double ay = 0.4 * std::cos(t * 1.1) + noise(gen);
        double az = 9.81 + 0.2 * std::sin(t * 1.3) + noise(gen);  // gravity plus shake
        return {ax, ay, az};
    }

private:
    std::random_device rd;
    std::mt19937 gen;
    std::uniform_real_distribution<double> noise;
};

int main() {
    AirMotion motion;
    std::cout << "Air Motion started (press Ctrl+C to stop)\n";
    while (true) {
        auto v = motion.sample();
        std::cout << "X: " << v.x << "  Y: " << v.y << "  Z: " << v.z << std::endl;
        // In a real system you would send this over WebSocket/serial to the server.
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    return 0;
}