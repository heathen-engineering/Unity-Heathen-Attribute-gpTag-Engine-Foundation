namespace Heathen.HATE
{
    /// <summary>
    /// A deterministic, seedable pseudo-random generator for HATE (HATE-Spec §10): the source for
    /// <c>Chance</c> predicates, ranged effect magnitudes, and any resolver randomisation. It is a
    /// <b>value type</b> so a world, a stream, or a per-entity substream each carries its own reproducible
    /// state cheaply, and it uses <b>integer-only</b> maths (SplitMix64 seeding + xoshiro256**) so the
    /// sequence is identical on every platform — the property netcode-authoritative / replay determinism
    /// depends on. HATE never uses <see cref="System.Random"/> (not stability-guaranteed across runtimes).
    /// </summary>
    public struct HateRng
    {
        private ulong _s0, _s1, _s2, _s3;

        /// <summary>Seed the generator. Any seed (including 0) yields a well-distributed state via SplitMix64.</summary>
        public HateRng(ulong seed)
        {
            // SplitMix64 expands the single seed into the four 64-bit words xoshiro256** needs.
            _s0 = SplitMix64(ref seed);
            _s1 = SplitMix64(ref seed);
            _s2 = SplitMix64(ref seed);
            _s3 = SplitMix64(ref seed);
        }

        private static ulong SplitMix64(ref ulong x)
        {
            ulong z = (x += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

        /// <summary>The next 64 random bits (xoshiro256** — fast, high quality, full period).</summary>
        public ulong NextULong()
        {
            ulong result = Rotl(_s1 * 5UL, 7) * 9UL;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = Rotl(_s3, 45);
            return result;
        }

        /// <summary>The next 32 random bits.</summary>
        public uint NextUInt() => (uint)(NextULong() >> 32);

        /// <summary>A double in <c>[0, 1)</c> using the top 53 bits (full mantissa precision).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>A float in <c>[0, 1)</c>.</summary>
        public float NextFloat() => (float)NextDouble();

        /// <summary>
        /// True with probability <paramref name="p"/> (clamped: p&lt;=0 is never, p&gt;=1 is always). Draws one
        /// value only when the outcome is not already decided, so the stream is not perturbed by trivial checks.
        /// </summary>
        public bool Chance(double p)
        {
            if (p <= 0.0) return false;
            if (p >= 1.0) return true;
            return NextDouble() < p;
        }

        /// <summary>An int in <c>[minInclusive, maxExclusive)</c>. Returns <paramref name="minInclusive"/> if the range is empty.</summary>
        public int RangeInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            ulong span = (ulong)((long)maxExclusive - minInclusive);
            return minInclusive + (int)(NextULong() % span);
        }

        /// <summary>A double in <c>[min, max)</c> (returns <paramref name="min"/> if the range is empty).</summary>
        public double RangeDouble(double min, double max)
            => max <= min ? min : min + NextDouble() * (max - min);

        /// <summary>A float in <c>[min, max)</c>.</summary>
        public float RangeFloat(float min, float max) => (float)RangeDouble(min, max);

        /// <summary>
        /// Derive an independent, reproducible substream from this generator + a <paramref name="streamId"/>
        /// (e.g. an entity id or ability tag hash). Same parent state + same id always yields the same child,
        /// so per-entity randomness is deterministic without threading one global stream through everything.
        /// Does not advance this generator.
        /// </summary>
        public HateRng Fork(ulong streamId)
        {
            ulong mixed = _s0 ^ (streamId + 0x9E3779B97F4A7C15UL);
            return new HateRng(mixed ^ Rotl(_s3 ^ streamId, 32));
        }
    }
}
