using ClassHub_API.Controllers;
using ClassHub_API.Data;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
var count=0;
void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS: "+name);}
var method=typeof(OperationsController).GetMethod("ConfirmManual")!;
Check(method.GetCustomAttributes(typeof(AuthorizeAttribute),false).Cast<AuthorizeAttribute>().Single().Roles=="ADMIN","Manual confirmation is admin-only");
Check(ManualReturnRules.Validate("RETURNING","LOCKED",false,null,"RETURN","ISSUED")==null,"No camera, RFID or closed-QR evidence required for manual return");
Check(ManualReturnRules.Validate("RETURNING","LOCKED",false,false,"RETURN","CLOSED")==null,"Closed return can be completed manually");
foreach(var status in new[]{"PENDING","ACTIVE","IN_USE","CANCELED"})
    Check(ManualReturnRules.Validate(status,"LOCKED",false,false,null,null)!=null,"Reject non-returning "+status);
Check(ManualReturnRules.Validate("RETURNING",null,false,false,null,null)!=null,"Missing cabinet rejected");
Check(ManualReturnRules.Validate("RETURNING","UNLOCKED",false,false,null,null)!=null,"Unlocked cabinet rejected");
Check(ManualReturnRules.Validate("RETURNING","LOCKED",true,false,null,null)!=null,"Open session rejected");
Check(ManualReturnRules.Validate("RETURNING","LOCKED",false,true,null,null)!=null,"Open physical door rejected");
foreach(var state in new[]{"USED","OPENED"})
    Check(ManualReturnRules.Validate("RETURNING","LOCKED",false,false,"RETURN",state)!=null,"In-flight return "+state+" rejected");
using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options);
var controller=new OperationsController(db,null!){ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext()}};
Check(await controller.ConfirmManual(1,new()) is UnauthorizedResult,"Missing actor rejected before DB access");
controller.HttpContext.User=new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.NameIdentifier,"test-admin")},"fixture"));
Check(await controller.ConfirmManual(1,new()) is BadRequestObjectResult,"Acknowledgement required before DB access");
Check(await controller.ConfirmManual(1,new(){Acknowledged=true,Condition="MAT_DO"}) is BadRequestObjectResult,"Missing equipment cannot be completed");
Check(await controller.ConfirmManual(1,new(){Acknowledged=true,Condition="HONG_HOC"}) is BadRequestObjectResult,"Damage needs a note");
Console.WriteLine($"PASS: {count} manual-return checks, no database opened");
