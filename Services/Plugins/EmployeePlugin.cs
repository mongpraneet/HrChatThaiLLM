using System.ComponentModel;
using Microsoft.SemanticKernel;
using HrChatThaiLLM.Server.Services;
using Microsoft.Extensions.Configuration;

namespace HrChatThaiLLM.Server.Services.Plugins;

/// <summary>
/// Plugin สำหรับข้อมูลพนักงานจากตาราง EMPDA, EmDept, EMDevi, EMLevel, EMPosi, EMCOMP
/// อัปเดตโครงสร้าง Pure Body Payload พร้อมสุ่มการแสดงผล 5 บริบท
/// </summary>
public class EmployeePlugin
{
    private readonly ISqlExecutorService _sql;
    private readonly string[] _managerLevels;
    public EmployeePlugin(ISqlExecutorService sql, IConfiguration config)
    {
        _sql = sql;

        // อ่านค่าจาก appsettings.json ถ้าไม่มีให้ใช้ค่าเริ่มต้น "M1,M2,M3,M4"
        var levelsStr = config["ManagerLevels"] ?? "M1,M2,M3,M4";
        _managerLevels = levelsStr.Split(',').Select(lvl => lvl.Trim()).ToArray();
    }

    // ─── ข้อมูลตัวเอง (สุ่ม 5 บริบทแยกตามสไตล์โครงสร้าง) ───────────────────────────

    [KernelFunction("get_my_profile")]
    [Description("ดูข้อมูลส่วนตัวของพนักงาน (ตัวเอง) รวมถึงชื่อ แผนก ฝ่าย ตำแหน่ง ระดับ บริษัท")]
    public async Task<string> GetMyProfile(
        [Description("รหัสพนักงาน")] string employeeId)
    {
        var sql = @"
            SELECT e.EMID, e.EMFNT AS EMFN, e.EMLNT AS EMLN,e.EMWD,
                   d.DPDST AS DeptName,
                   v.DVDST AS DeviName,
                   l.LVDST AS LevelName,
                   p.PSDST AS PositionName,
                   c.CMDST AS CompanyName
            FROM EMPDA e
            LEFT JOIN EmDept  d ON e.DPID = d.DPID AND e.CMID = d.CMID
            LEFT JOIN EMDevi  v ON e.DVID = v.DVID AND e.CMID = v.CMID
            LEFT JOIN EMLevel l ON e.LVID = l.LVID AND e.CMID = l.CMID
            LEFT JOIN EMPosi  p ON e.PSID = p.PSID AND e.CMID = p.CMID
            LEFT JOIN EMCOMP  c ON e.CMID = c.CMID
            WHERE e.EMID = @EmployeeId
        ";
        var r = await _sql.QueryFirstOrDefaultAsync<dynamic>(sql, new { EmployeeId = employeeId });
        if (r == null) return "❌ ไม่พบข้อมูลรายละเอียดพนักงานในระบบหลักค่ะ";

        // 🎲 ตัวเลือกสุ่ม Layout โครงสร้างใหญ่สำหรับข้อมูลส่วนตัวพนักงาน (5 บริบท)
        string[] profileFormats = [
            $"""
            👤 **ข้อมูลทะเบียนประวัติพนักงาน**
            • รหัสพนักงาน : {r.EMID}
            • ชื่อ-นามสกุล : {r.EMFN} {r.EMLN}
            • วันที่เข้างาน : {r.EMWD}
            • แผนก/สังกัด : {r.DeptName}
            • ฝ่ายงาน     : {r.DeviName}
            • ตำแหน่งงาน  : {r.PositionName}
            • ระดับพนักงาน : {r.LevelName}
            • บริษัทในเครือ : {r.CompanyName}
            """,
            $"""
            🔹 **ประวัติข้อมูลบุคคล (พนักงานทั่วไป)**
            » พนักงาน : `{r.EMID}` 🆔
            » ชื่อผู้ใช้งาน : `{r.EMFN} {r.EMLN}` 🏷️
            » วันที่เข้างาน : `{r.EMWD}` 📅
            » สังกัดแผนก : `{r.DeptName}` 🏢
            » ฝ่ายปฏิบัติการ : `{r.DeviName}` 🏬
            » ตำแหน่ง : `{r.PositionName}` 💼
            » ระดับ : `{r.LevelName}` 📊
            » บริษัท : `{r.CompanyName}` 🏭
            """,
            $"""
            📌 **แผงข้อมูลสารสนเทศพนักงานประจำ**
            ┌──────────────────────────────────────────┐
            │ 🛠️ รหัสพนักงาน: {r.EMID}
            │ 👤 ชื่อ-นามสกุล: {r.EMFN} {r.EMLN}
            │ 📅 วันที่เข้างาน: {r.EMWD}
            │ 🏢 แผนกงาน: {r.DeptName}
            │ 🏬 สายฝ่าย: {r.DeviName}
            │ 💼 ตำแหน่ง: {r.PositionName}
            │ 📈 ระดับ: {r.LevelName}
            │ 🏭 สังกัดบริษัท: {r.CompanyName}
            └──────────────────────────────────────────┘
            """,
            $"""
            📁 **แฟ้มประวัติประมวลผลข้อมูลส่วนตัว (HR Profile)**
            ➔ [รหัส] : {r.EMID}
            ➔ [ชื่อ] : {r.EMFN} {r.EMLN} 🎖️
            ➔ [วันที่เข้างาน] : {r.EMWD} 
            ➔ [แผนก] : {r.DeptName}
            ➔ [ฝ่าย] : {r.DeviName}
            ➔ [ตำแหน่ง] : {r.PositionName}
            ➔ [ระดับ] : {r.LevelName}
            ➔ [บริษัท] : {r.CompanyName}
            """,
            $"""
            ✨ **สรุปข้อมูลทะเบียนพนักงานอิเล็กทรอนิกส์**
            ⭐ รหัสประจำตัว: **{r.EMID}**
            ⭐ นามพนักงาน: **{r.EMFN} {r.EMLN}**
            ⭐ วันที่เข้างาน: {r.EMWD}
            ⭐ ส่วนงานแผนก: {r.DeptName} ({r.DeviName})
            ⭐ หน้าที่ตำแหน่ง: {r.PositionName} | ระดับ: {r.LevelName}
            ⭐ ภายใต้บริษัท: {r.CompanyName}
            """
        ];

        return profileFormats[Random.Shared.Next(5)];
    }

    [KernelFunction("get_my_id_card")]
    [Description("ดูเลขบัตรประชาชนของตัวเอง (ดูได้เฉพาะตัวเองเท่านั้น)")]
    public async Task<string> GetMyIdCard(
        [Description("รหัสพนักงาน (ต้องเป็นตัวเองเท่านั้น)")] string employeeId)
    {
        var sql = "SELECT IDNO FROM EMPDA WHERE EMID = @EmployeeId";
        var r = await _sql.QueryFirstOrDefaultAsync<dynamic>(sql, new { EmployeeId = employeeId });
        if (r == null) return "❌ ไม่พบข้อมูลเลขประจำตัวพนักงานค่ะ";

        string idno = r.IDNO?.ToString() ?? "";
        if (idno.Length >= 4)
            idno = new string('X', idno.Length - 4) + idno.Substring(idno.Length - 4);

        // 🎲 สุ่มรูปแบบการเซนเซอร์และรายงานเลขบัตร (5 บริบท)
        string[] idFormats = [
            $"💳 **ข้อมูลบัตรประจำตัวประชาชน**: {idno} (แสดงเฉพาะ 4 หลักสุดท้ายเพื่อความปลอดภัย)",
            $"🆔 **เลขประจำตัว 13 หลัก (ความปลอดภัยสูง)**: `{idno}` 🛡️",
            $"🔒 *ระบบคุ้มครองข้อมูลส่วนบุคคล (PDPA):* หมายเลขบัตรประชาชนของคุณคือ **{idno}**",
            $"📌 **National ID (Masked Data)** » {idno} 📁",
            $"🔑 หมายเลขประจำตัวผู้เสียภาษี/บัตรประชาชน (4 หลักท้าย): `{idno}`"
        ];

        return idFormats[Random.Shared.Next(5)];
    }

    // ─── ข้อมูลเพื่อนร่วมแผนก และ Master Data (วนลูปตามสไตล์สุ่มแบบ 5 บริบท) ───────────────────

    [KernelFunction("get_dept_colleagues")]
    [Description("ดูรายชื่อพนักงานในแผนกเดียวกัน (ไม่แสดงบัตรประชาชน)")]
    public async Task<string> GetDeptColleagues(
        [Description("รหัสพนักงาน")] string employeeId)
    {
        var sql = @"
            SELECT e2.EMID, e2.EMFNT AS EMFN, e2.EMLNT AS EMLN,
                   p.PSDST AS PositionName,
                   l.LVDST AS LevelName
            FROM EMPDA e1
            INNER JOIN EMPDA e2 ON e1.DPID = e2.DPID AND e1.CMID = e2.CMID
            LEFT JOIN EMPosi p ON e2.PSID = p.PSID AND e2.CMID = p.CMID
            LEFT JOIN EMLevel l ON e2.LVID = l.LVID AND e2.CMID = l.CMID
            WHERE e1.EMID = @EmployeeId
            ORDER BY e2.EMID
        ";
        var rows = await _sql.QueryAsync<dynamic>(sql, new { EmployeeId = employeeId });
        if (!rows.Any()) return "👥 ไม่พบข้อมูลรายชื่อพนักงานร่วมสังกัดแผนกงานนี้ในระบบค่ะ";

        int layoutStyleIndex = Random.Shared.Next(5);
        var lines = rows.Select(r => FormatListRow(layoutStyleIndex, r.EMID, $"{r.EMFN} {r.EMLN}", (string)r.PositionName, (string)r.LevelName));
        string sectionBody = string.Join("\n", lines);

        string[] containerFormats = [
            $"👥 **รายชื่อเพื่อนร่วมงานสังกัดแผนกเดียวกัน**\n{sectionBody}",
            $"🏢 **ทำเนียบบุคลากรภายในหน่วยงาน (Colleagues)**\n{sectionBody}",
            $"┌ 👥 **สรุปรายชื่อทีมงานและเพื่อนร่วมแผนก**\n{string.Join("\n", lines.Select(l => "│ " + l))}\n└ 📊 รวมทีมงานทั้งสิ้น: {rows.Count()} คน",
            $"💼 **สมาชิกทีมปฏิบัติการภายใต้สังกัดเดียวกัน**\n{sectionBody}",
            $"➢ **รายนามพนักงานและโครงสร้างทีมร่วมแผนก** 📂\n{sectionBody}"
        ];

        return containerFormats[layoutStyleIndex];
    }

    [KernelFunction("get_departments")]
    [Description("ดูรายชื่อแผนกทั้งหมดในบริษัท")]
    public async Task<string> GetDepartments(
         [Description("รหัสพนักงาน")] string employeeId)
    {
        var sql = "SELECT DPID, DPDST FROM EmDept ORDER BY DPID";
        var rows = await _sql.QueryAsync<dynamic>(sql, null);
        if (!rows.Any()) return "🏢 ไม่พบโครงสร้างแผนกงานในฐานข้อมูลค่ะ";

        int totalCount = rows.Count();
        int layoutStyleIndex = Random.Shared.Next(5);
        var lines = rows.Select(r => FormatMasterRow(layoutStyleIndex, (string)r.DPID, (string)r.DPDST));
        string sectionBody = string.Join("\n", lines);

        string[] headers = [
            $"""
            🏢 **โครงสร้างรายชื่อแผนกงานทั้งหมดภายในองค์กร**
            {sectionBody}
            📊 **รวมทั้งหมด**: {totalCount} แผนก
            """,
            $"""
            📊 **ทำเนียบบรหัสแผนกปฏิบัติงานหลัก (Department List)**
            {sectionBody}
            📌 **สรุปยอดรวมหน่วยงาน**: ทั้งหมด {totalCount} แผนกประจำการ
            """,
            $"""
            ┌ 🏢 **ผังรายชื่อแผนกงานจัดตั้งในบริษัท**
            {string.Join("\n", lines.Select(l => "│ " + l))}
            └ 📉 **ฐานข้อมูลระบุ**: มีกลุ่มงานรวมทั้งหมด {totalCount} แผนก
            """,
            $"""
            💼 **หมวดหมู่รหัสแผนกประจำการ (Master Data)**
            {sectionBody}
            🏢 **Total Departments**: รวมทั้งสิ้น {totalCount} แผนกในระบบ
            """,
            $"""
            ➔ **รายชื่อกลุ่มงานและรหัสแผนกปฏิบัติการทั้งหมด** 📂
            {sectionBody}
            ✨ มีโครงสร้างส่วนงานที่เปิดใช้งาน **รวมทั้งหมด {totalCount} แผนก**
            """
        ];

        return headers[layoutStyleIndex];
    }

    [KernelFunction("get_divisions")]
    [Description("ดูรายชื่อฝ่ายทั้งหมดในบริษัท")]
    public async Task<string> GetDivisions(
        [Description("รหัสพนักงาน")] string employeeId)
    {
        var sql = "SELECT DVID, DVDST FROM EMDevi ORDER BY DVID";
        var rows = await _sql.QueryAsync<dynamic>(sql, null);
        if (!rows.Any()) return "🏬 ไม่พบข้อมูลโครงสร้างสายฝ่ายงานในฐานข้อมูลค่ะ";

        int totalCount = rows.Count();
        int layoutStyleIndex = Random.Shared.Next(5);
        var lines = rows.Select(r => FormatMasterRow(layoutStyleIndex, (string)r.DVID, (string)r.DVDST));
        string sectionBody = string.Join("\n", lines);

        string[] headers = [
            $"""
            🏬 **รายชื่อฝ่ายงานหลักทั้งหมดในโครงสร้างองค์กร**
            {sectionBody}
            📊 **รวมทั้งหมด**: {totalCount} ฝ่าย
            """,
            $"""
            📊 **รหัสฝ่ายและกลุ่มสายงานบริหาร (Division Master)**
            {sectionBody}
            📌 **สรุปยอดรวมสายงาน**: ทั้งหมด {totalCount} ฝ่ายปฏิบัติการ
            """,
            $"""
            ┌ 🏬 **ผังรายชื่อสายฝ่ายปฏิบัติการ**
            {string.Join("\n", lines.Select(l => "│ " + l))}
            └ 📉 **ฐานข้อมูลระบุ**: มีโครงสร้างแกนหลักรวมทั้งหมด {totalCount} ฝ่าย
            """,
            $"""
            📌 **หมวดหมู่รหัสฝ่ายงานประจำโครงสร้าง (Master Data)**
            {sectionBody}
            🏬 **Total Divisions**: รวมทั้งสิ้น {totalCount} ฝ่ายในระบบ
            """,
            $"""
            ➔ **รายชื่อกลุ่มสายฝ่ายงานหลักทั้งหมดในเครือ** 📂
            {sectionBody}
            ✨ มีโครงสร้างสายงานที่เปิดใช้งาน **รวมทั้งหมด {totalCount} ฝ่าย**
            """
        ];

        return headers[layoutStyleIndex];
    }

    [KernelFunction("get_levels")]
    [Description("ดูรายชื่อระดับพนักงานทั้งหมด")]
    public async Task<string> GetLevels(
        [Description("รหัสพนักงาน")] string employeeId)
    {
        var sql = "SELECT LVID, LVDST FROM EMLevel ORDER BY LVID";
        var rows = await _sql.QueryAsync<dynamic>(sql, null);
        if (!rows.Any()) return "📊 ไม่พบข้อมูลระดับพนักงานในโครงสร้างบริษัทค่ะ";

        int layoutStyleIndex = Random.Shared.Next(5);
        var lines = rows.Select(r => FormatMasterRow(layoutStyleIndex, (string)r.LVID, (string)r.LVDST));
        string sectionBody = string.Join("\n", lines);

        string[] headers = [
            $"📊 **โครงสร้างระดับและเกรดขั้นพนักงานทั้งหมด (Grade Level)**\n{sectionBody}",
            $"📈 **ลำดับขั้นตำแหน่งและโครงสร้างระดับพนักงานประจำ**\n{sectionBody}",
            $"📊 **ตารางทำเนียบระดับชั้นพนักงานองค์กร**\n{string.Join("\n", lines.Select(l => "│ " + l))}",
            $"🎖️ **โครงสร้างระดับเลเวลพนักงาน (Master Data)**\n{sectionBody}",
            $"➔ **ผังรหัสเกรดและระดับชั้นประเมินของบุคลากร** 📂\n{sectionBody}"
        ];

        return headers[layoutStyleIndex];
    }

    [KernelFunction("get_positions")]
    [Description("ดูรายชื่อตำแหน่งงานทั้งหมดในบริษัท")]
    public async Task<string> GetPositions(
        [Description("รหัสพนักงาน")] string employeeId)
    {
        var sql = "SELECT PSID, PSDST FROM EMPosi ORDER BY PSID";
        var rows = await _sql.QueryAsync<dynamic>(sql, null);
        if (!rows.Any()) return "💼 ไม่พบฐานข้อมูลรายชื่อตำแหน่งงานในระบบค่ะ";

        int layoutStyleIndex = Random.Shared.Next(5);
        var lines = rows.Select(r => FormatMasterRow(layoutStyleIndex, (string)r.PSID, (string)r.PSDST));
        string sectionBody = string.Join("\n", lines);

        string[] headers = [
            $"💼 **รายชื่อตำแหน่งงานและวิชาชีพทั้งหมดประจำบริษัท**\n{sectionBody}",
            $"🛠️ **ทำเนียบรหัสตำแหน่งงานพนักงาน**\n{sectionBody}",
            $"💼 **โครงสร้างชื่อตำแหน่งงานในระบบบริหารบุคคล**\n{string.Join("\n", lines.Select(l => "│ " + l))}",
            $"📌 **หมวดหมู่สายงานและรหัสตำแหน่งงานพนักงาน**\n{sectionBody}",
            $"➔ **รายนามตำแหน่งงานและหน้าที่ความรับผิดชอบทั้งหมด** 📂\n{sectionBody}"
        ];

        return headers[layoutStyleIndex];
    }

    [KernelFunction("get_company_info")]
    [Description("ดูข้อมูลบริษัทในเครือ")]
    public async Task<string> GetCompanyInfo(
        [Description("รหัสพนักงาน")] string employeeId)
    {
        var sql = "SELECT CMID, CMDST FROM EMCOMP ORDER BY CMID";
        var rows = await _sql.QueryAsync<dynamic>(sql, null);
        if (!rows.Any()) return "🏭 ไม่พบข้อมูลรายชื่อบริษัทในเครือข่ายองค์กรค่ะ";

        int layoutStyleIndex = Random.Shared.Next(5);
        var lines = rows.Select(r => FormatMasterRow(layoutStyleIndex, (string)r.CMID, (string)r.CMDST));
        string sectionBody = string.Join("\n", lines);

        string[] headers = [
            $"🏭 **ทำเนียบรายชื่อบริษัททั้งหมดที่อยู่ในเครือข่ายองค์กร**\n{sectionBody}",
            $"🏢 **เครือบริษัทและนิติบุคคลภายใต้การบริหาร**\n{sectionBody}",
            $"🏭 **โครงสร้างกลุ่มบริษัทร่วมในเครือระบบงาน**\n{string.Join("\n", lines.Select(l => "│ " + l))}",
            $"💎 **รายชื่อองค์กรนิติบุคคลและบริษัทร่วมทุน**\n{sectionBody}",
            $"➔ **ข้อมูลรายชื่อนิติบุคคลและเครือข่ายอุตสาหกรรม** 📂\n{sectionBody}"
        ];

        return headers[layoutStyleIndex];
    }

    [KernelFunction("get_my_managers")]
    [Description("ดูรายชื่อหัวหน้างาน ผู้จัดการ หรือผู้บังคับบัญชาสายตรงในแผนกเดียวกัน")]
    public async Task<string> GetMyManagers([Description("รหัสพนักงาน")] string employeeId)
    {
        // 🔥 เปลี่ยน e2.LVID IN (...) เป็น e2.LVID IN @ManagerLevels
        var sql = @"
            SELECT e2.EMID, e2.EMFNT AS EMFN, e2.EMLNT AS EMLN,
                   p.PSDST AS PositionName,
                   l.LVDST AS LevelName
            FROM EMPDA e1
            INNER JOIN EMPDA e2 ON e1.DPID = e2.DPID AND e1.CMID = e2.CMID
            LEFT JOIN EMPosi p ON e2.PSID = p.PSID AND e2.CMID = p.CMID
            LEFT JOIN EMLevel l ON e2.LVID = l.LVID AND e2.CMID = l.CMID
            WHERE e1.EMID = @EmployeeId 
              AND e2.LVID IN @ManagerLevels 
            ORDER BY e2.LVID ASC, e2.EMID ASC
        ";

        // 🔥 ส่งตัวแปร _managerLevels เข้าไปใน Dapper (Dapper จะแปลง Array เป็น IN clause ให้เองอัตโนมัติ)
        var rows = await _sql.QueryAsync<dynamic>(sql, new
        {
            EmployeeId = employeeId,
            ManagerLevels = _managerLevels
        });

        if (!rows.Any()) return "👑 ไม่พบข้อมูลรายชื่อหัวหน้างานหรือผู้จัดการในสังกัดแผนกนี้ค่ะ";

        int layoutStyleIndex = Random.Shared.Next(5);
        var lines = rows.Select(r => FormatListRow(layoutStyleIndex, r.EMID, $"{r.EMFN} {r.EMLN}", (string)r.PositionName, (string)r.LevelName));
        string sectionBody = string.Join("\n", lines);

        string[] containerFormats = [
            $"👑 **รายชื่อหัวหน้างานและผู้จัดการประจำแผนก**\n{sectionBody}",
            $"💼 **ทำเนียบผู้บริหารและหัวหน้างานสายตรง (Managers)**\n{sectionBody}",
            $"┌ 👑 **สรุปรายชื่อสายการบังคับบัญชาในแผนก**\n{string.Join("\n", lines.Select(l => "│ " + l))}\n└ 📊 รวมหัวหน้างานทั้งหมด: {rows.Count()} ท่าน",
            $"🛡️ **รายนามผู้บังคับบัญชาภายใต้สังกัดกลุ่มงาน**\n{sectionBody}",
            $"➢ **รายชื่อหัวหน้าและผู้จัดการสายปฏิบัติการ** 📂\n{sectionBody}"
        ];

        return containerFormats[layoutStyleIndex];
    }

    // ── 🎲 ฟังก์ชันจัดสไตล์ย่อยเพื่อแสดงผล Line Items 5 บริบท (Row-Level Format Randomizers) ──

    private static string FormatListRow(int style, string id, string name, string pos, string lvl)
    {
        return style switch
        {
            0 => $"• รหัส: {id} │ ชื่อ: {name} │ ตำแหน่ง: {pos} │ ระดับ: {lvl}",
            1 => $"🔹 `[{id}]` **{name}** ➔ สายงาน: `{pos}` │ ระดับ: `{lvl}`",
            2 => $"📌 **พนักงาน ID {id}** : {name} [ {pos} / ระดับ: {lvl} ]",
            3 => $"» {id} | {name} » {pos} ({lvl}) 📂",
            _ => $"➢ รหัสพนักงาน: **{id}** — {name} ({pos} | ระดับ: {lvl}) 🎖️"
        };
    }

    private static string FormatMasterRow(int style, string id, string name)
    {
        return style switch
        {
            0 => $"• รหัส: {id} │ รายละเอียด: {name}",
            1 => $"🔹 `[{id}]` ➔ สังกัด: `{name}`",
            2 => $"📌 **ID: {id}** — {name}",
            3 => $"» รหัสพนักงาน {id} » {name} 📂",
            _ => $"➢ [รหัส: {id}] — {name} ✨"
        };
    }
}