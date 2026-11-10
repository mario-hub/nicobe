using System.Globalization;

namespace NicoBE;

internal static class CommentMotion
{
    internal static double LengthSpeedMultiplier(string text)
    {
        int longest=0;
        foreach(string line in text.Split('\r','\n'))
        {
            var elements=StringInfo.GetTextElementEnumerator(line);int count=0;
            while(count<50 && elements.MoveNext())count++;
            longest=Math.Max(longest,count);
            if(longest==50)break;
        }
        return 1+longest*.02;
    }
}
