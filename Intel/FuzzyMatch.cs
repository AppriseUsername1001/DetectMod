namespace EVEAA.Mod.Intel;

/// <summary>오타 허용 매칭용 편집거리 계산. RIFT의 퍼지 성계/함선 매칭을 참고해 추가.</summary>
internal static class FuzzyMatch
{
    /// <summary>Levenshtein distance, capped at maxDistance+1 (길이차만으로 조기 컷).</summary>
    public static int Distance(string a, string b, int maxDistance)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > maxDistance) return maxDistance + 1;
        if (la == 0) return lb;
        if (lb == 0) return la;

        var prev = new int[lb + 1];
        var curr = new int[lb + 1];
        for (int j = 0; j <= lb; j++) prev[j] = j;

        for (int i = 1; i <= la; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];
            char ca = char.ToLowerInvariant(a[i - 1]);
            for (int j = 1; j <= lb; j++)
            {
                char cb = char.ToLowerInvariant(b[j - 1]);
                int cost = ca == cb ? 0 : 1;
                int del = prev[j] + 1;
                int ins = curr[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                int v = Math.Min(del, Math.Min(ins, sub));
                curr[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > maxDistance) return maxDistance + 1;
            (prev, curr) = (curr, prev);
        }
        return Math.Min(prev[lb], maxDistance + 1);
    }
}
