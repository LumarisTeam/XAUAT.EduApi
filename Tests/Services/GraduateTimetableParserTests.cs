using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// 研究生课表解析器：输入是 <c>src/parse_html.py</c> 处理的那种
/// "JS 单元格赋值"页面，输出与本科生同构的 <c>CourseActivity</c>。
/// </summary>
public class GraduateTimetableParserTests
{
    private const string SampleHtml = """
        <html><body><script type="text/javascript">
        <!--
        var td = document.getElementById("td_1_41");
        td.innerHTML += "课程:高等数学<br>班级:01<br>主讲教师:张三<br>教室:草堂校区-南山书院-南山书院303(第1-16周)";
        var td = document.getElementById("td_1_42");
        td.innerHTML += "课程:高等数学<br>班级:01<br>主讲教师:张三<br>教室:草堂校区-南山书院-南山书院303(第1-16周)";
        var td = document.getElementById("td_1_43");
        td.innerHTML += "课程:高等数学<br>班级:01<br>主讲教师:张三<br>教室:草堂校区-南山书院-南山书院303(第1-16周)";
        var td = document.getElementById("td_3_47");
        td.innerHTML += "课程:研究生英语<br>班级:02<br>主讲教师:李四<br>教室:雁塔校区-教学大楼-教学大楼101(第2-8周,双周)";
        var td = document.getElementById("td_2_51");
        td.innerHTML += "课程:学术道德<br>主讲教师:&nbsp;王五<br>教室:草堂校区-图书馆-报告厅";
        var td = document.getElementById("td_4_45");
        td.innerHTML += "课程:数值分析<br>主讲教师:赵六<br>教室:草堂校区-教学楼-101(第1-4周)";
        var td = document.getElementById("td_4_47");
        td.innerHTML += "课程:数值分析<br>主讲教师:赵六<br>教室:草堂校区-教学楼-101(第1-4周)";
        var td = document.getElementById("td_9_99");
        td.innerHTML += "课程:不应出现";
        hb_cell();
        //-->
        </script></body></html>
        """;

    [Fact]
    public void Parse_ShouldMergeContiguousUnitsIntoOneActivity()
    {
        var courses = GraduateTimetableParser.Parse(SampleHtml);

        var math = Assert.Single(courses, course => course.CourseName == "高等数学");
        Assert.Equal(1, math.Weekday);
        Assert.Equal(1, math.StartUnit);
        Assert.Equal(3, math.EndUnit);
        Assert.Equal("草堂校区", math.Campus);
        Assert.Equal("南山书院303", math.Room);
        Assert.Equal(new[] { "张三" }, math.Teachers);
        Assert.Equal(Enumerable.Range(1, 16), math.WeekIndexes);
    }

    [Fact]
    public void Parse_ShouldTranslatePeriodCodeToContinuousUnit()
    {
        var courses = GraduateTimetableParser.Parse(SampleHtml);

        var english = Assert.Single(courses, course => course.CourseName == "研究生英语");
        Assert.Equal(3, english.Weekday);
        // 47 = 下午第 1 节 -> 全天第 7 节
        Assert.Equal(7, english.StartUnit);
        Assert.Equal(7, english.EndUnit);
        Assert.Equal("雁塔校区", english.Campus);
        Assert.Equal("教学大楼101", english.Room);
        Assert.Equal(Enumerable.Range(2, 7), english.WeekIndexes);
    }

    [Fact]
    public void Parse_ShouldKeepNonContiguousUnitsAsSeparateActivities()
    {
        var courses = GraduateTimetableParser.Parse(SampleHtml);

        var analysis = courses.Where(course => course.CourseName == "数值分析")
            .OrderBy(course => course.StartUnit)
            .ToList();

        Assert.Equal(2, analysis.Count);
        Assert.Equal((5, 5), (analysis[0].StartUnit, analysis[0].EndUnit));
        Assert.Equal((7, 7), (analysis[1].StartUnit, analysis[1].EndUnit));
    }

    [Fact]
    public void Parse_ShouldKeepCourseWithoutWeekRange_AndDecodeNbsp()
    {
        var courses = GraduateTimetableParser.Parse(SampleHtml);

        var ethics = Assert.Single(courses, course => course.CourseName == "学术道德");
        Assert.Equal(2, ethics.Weekday);
        // 51 = 晚上第 1 节 -> 全天第 11 节
        Assert.Equal(11, ethics.StartUnit);
        Assert.Equal(new[] { "王五" }, ethics.Teachers);
        Assert.Empty(ethics.WeekIndexes);
    }

    [Fact]
    public void Parse_ShouldIgnoreUnknownPeriodCodes()
    {
        var courses = GraduateTimetableParser.Parse(SampleHtml);

        Assert.DoesNotContain(courses, course => course.CourseName == "不应出现");
    }

    [Fact]
    public void Parse_ShouldUnescapeJavaScriptStringLiterals()
    {
        const string html = """
            var td = document.getElementById("td_5_41");
            td.innerHTML += "课程:数据库\"原理\"<br>主讲教师:钱七<br>教室:草堂校区-教学楼-A201(第1-2周)";
            hb_cell();
            """;

        var course = Assert.Single(GraduateTimetableParser.Parse(html));

        Assert.Equal("数据库\"原理\"", course.CourseName);
        Assert.Equal("A201", course.Room);
        Assert.Equal(new[] { 1, 2 }, course.WeekIndexes);
    }

    /// <summary>
    /// 教室字段的校区/教室拆分必须与参考实现 <c>src/parse_html.py</c> 的 <c>_split_room</c> 一致：
    /// 最后一段是纯房间号时，楼栋简称在倒数第二段，要拼回来。
    /// </summary>
    [Theory]
    [InlineData("草堂校区-草堂校区9号楼-草堂9-208", "草堂校区", "草堂9-208")]
    [InlineData("草堂校区-草堂校区9号楼-草堂9-208A", "草堂校区", "草堂9-208A")]
    [InlineData("草堂校区-教学楼-101", "草堂校区", "教学楼-101")]
    [InlineData("草堂校区-草堂校区南山书院-南山书院303", "草堂校区", "南山书院303")]
    [InlineData("草堂校区-教学楼-A201", "草堂校区", "A201")]
    [InlineData("草堂校区-图书馆-报告厅", "草堂校区", "报告厅")]
    [InlineData("报告厅", "报告厅", "未知")]
    public void Parse_ShouldSplitClassroomLikeReferenceImplementation(
        string classroom, string expectedCampus, string expectedRoom)
    {
        var html = $$"""
            var td = document.getElementById("td_1_41");
            td.innerHTML += "课程:测试<br>主讲教师:张三<br>教室:{{classroom}}(第1-2周)";
            hb_cell();
            """;

        var course = Assert.Single(GraduateTimetableParser.Parse(html));

        Assert.Equal(expectedCampus, course.Campus);
        Assert.Equal(expectedRoom, course.Room);
    }

    [Fact]
    public void Parse_ShouldReturnEmpty_WhenHtmlIsBlank()
    {
        Assert.Empty(GraduateTimetableParser.Parse(""));
        Assert.Empty(GraduateTimetableParser.Parse("<html></html>"));
    }
}
