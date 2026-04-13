using WebApplication1;
using Xunit;

namespace WebApplication1.Tests;

public class UserInfoTests
{
    private static UserInfo MakeUser(string role) =>
        new(1, "test", "Test User", role, Array.Empty<string>(), Array.Empty<int>());

    [Fact]
    public void Admin_HasAllPermissions()
    {
        var u = MakeUser("Admin");
        Assert.True(u.IsAdmin);
        Assert.True(u.CanEditPlan);
        Assert.True(u.CanEditWorkload);
        Assert.True(u.CanEditFaculty);
        Assert.True(u.CanEditDisciplines);
        Assert.True(u.CanReviewDisciplineRequests);
    }

    [Fact]
    public void AcademicDirector_CanEditPlanAndDisciplines_ButNotWorkloadOrFaculty()
    {
        var u = MakeUser("AcademicDirector");
        Assert.False(u.IsAdmin);
        Assert.True(u.IsAcademicDirector);
        Assert.True(u.CanEditPlan);
        Assert.True(u.CanEditDisciplines);
        Assert.False(u.CanEditWorkload);
        Assert.False(u.CanEditFaculty);
        Assert.False(u.CanReviewDisciplineRequests);
    }

    [Fact]
    public void DepartmentManager_CanEditWorkloadAndFaculty_ButNotPlanOrDisciplines()
    {
        var u = MakeUser("DepartmentManager");
        Assert.False(u.IsAdmin);
        Assert.True(u.IsDepartmentManager);
        Assert.True(u.CanEditWorkload);
        Assert.True(u.CanEditFaculty);
        Assert.False(u.CanEditPlan);
        Assert.False(u.CanEditDisciplines);
        Assert.True(u.CanReviewDisciplineRequests);
    }

    [Fact]
    public void RegularUser_CannotEditAnything()
    {
        var u = MakeUser("User");
        Assert.False(u.IsAdmin);
        Assert.False(u.IsAcademicDirector);
        Assert.False(u.IsDepartmentManager);
        Assert.False(u.CanEditPlan);
        Assert.False(u.CanEditWorkload);
        Assert.False(u.CanEditFaculty);
        Assert.False(u.CanEditDisciplines);
        Assert.False(u.CanReviewDisciplineRequests);
    }

    [Fact]
    public void RoleComparison_IsCaseInsensitive()
    {
        Assert.True(MakeUser("admin").IsAdmin);
        Assert.True(MakeUser("ADMIN").IsAdmin);
        Assert.True(MakeUser(" Admin ").IsAdmin);
    }
}
