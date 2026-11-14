#include <ntifs.h>
#include <wdmsec.h>

NTKERNELAPI NTSTATUS NTAPI MmCopyMemory(PVOID TargetAddress, MM_COPY_ADDRESS SourceAddress, SIZE_T NumberOfBytes, ULONG Flags, PSIZE_T NumberOfBytesTransferred);
#define MM_COPY_MEMORY_VIRTUAL 0x2
NTKERNELAPI PVOID NTAPI PsGetProcessSectionBaseAddress(PEPROCESS Process);
NTKERNELAPI NTSTATUS NTAPI IoCreateDriver(PUNICODE_STRING DriverName, PDRIVER_INITIALIZE InitializationFunction);
#define WH_MAX_READ (1024u * 1024u)
#define WH_IOCTL_ATTACH CTL_CODE(FILE_DEVICE_UNKNOWN, 0xA00, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define WH_IOCTL_READ   CTL_CODE(FILE_DEVICE_UNKNOWN, 0xA01, METHOD_BUFFERED, FILE_READ_ACCESS)
#define WH_IOCTL_DETACH CTL_CODE(FILE_DEVICE_UNKNOWN, 0xA02, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define WH_IOCTL_BASE   CTL_CODE(FILE_DEVICE_UNKNOWN, 0xA03, METHOD_BUFFERED, FILE_READ_ACCESS)

typedef struct _WH_ATTACH { unsigned long pid; unsigned long reserved; } WH_ATTACH;
typedef struct _WH_READ { unsigned __int64 address; unsigned long size; unsigned long reserved; } WH_READ;
typedef struct _WH_RESULT { long status; unsigned long transferred; unsigned long reserved; } WH_RESULT;

C_ASSERT(sizeof(WH_ATTACH) == 8);
C_ASSERT(sizeof(WH_READ) == 16);
C_ASSERT(sizeof(WH_RESULT) == 12);

static const GUID WhClass = {0x8e4c1f21, 0x3a6b, 0x4c2d, {0x9b, 0x21, 0x7e, 0x4a, 0x55, 0x11, 0x9c, 0x30}};

static PEPROCESS WhGetProcess(PFILE_OBJECT file) { return (PEPROCESS)file->FsContext; }

static VOID WhPutProcess(PFILE_OBJECT file)
{
    PEPROCESS process = WhGetProcess(file);
    if (process) {
        file->FsContext = NULL;
        ObDereferenceObject(process);
    }
}

static NTSTATUS WhAttach(PIRP irp, PIO_STACK_LOCATION stack)
{
    WH_ATTACH* request;
    PEPROCESS process = NULL;
    NTSTATUS status;

    if (stack->Parameters.DeviceIoControl.InputBufferLength != sizeof(WH_ATTACH) ||
        !irp->AssociatedIrp.SystemBuffer || irp->RequestorMode != UserMode) {
        return STATUS_INVALID_PARAMETER;
    }
    request = (WH_ATTACH*)irp->AssociatedIrp.SystemBuffer;
    if (!request->pid || request->reserved) return STATUS_INVALID_PARAMETER;

    status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)request->pid, &process);
    if (!NT_SUCCESS(status)) return status;

    WhPutProcess(stack->FileObject);
    stack->FileObject->FsContext = process;
    return STATUS_SUCCESS;
}

static NTSTATUS WhRead(PIRP irp, PIO_STACK_LOCATION stack)
{
    WH_READ* request;
    WH_RESULT* result;
    PEPROCESS process;
    KAPC_STATE apc;
    MM_COPY_ADDRESS source;
    ULONG64 address;
    ULONG size;
    NTSTATUS status;
    SIZE_T copied = 0;
    ULONG_PTR highest = (ULONG_PTR)MmHighestUserAddress;

    if (stack->Parameters.DeviceIoControl.InputBufferLength != sizeof(WH_READ) ||
        !irp->AssociatedIrp.SystemBuffer) return STATUS_INVALID_PARAMETER;

    request = (WH_READ*)irp->AssociatedIrp.SystemBuffer;
    address = request->address;
    size = request->size;
    if (!size || size > WH_MAX_READ || request->reserved || !address ||
        address > highest || size - 1 > highest - (ULONG_PTR)address) {
        return STATUS_INVALID_PARAMETER;
    }
    if (stack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(WH_RESULT) + size)
        return STATUS_BUFFER_TOO_SMALL;

    process = WhGetProcess(stack->FileObject);
    if (!process) return STATUS_INVALID_DEVICE_STATE;

    result = (WH_RESULT*)irp->AssociatedIrp.SystemBuffer;
    RtlZeroMemory(result, sizeof(WH_RESULT));
    source.VirtualAddress = (PVOID)(ULONG_PTR)address;

    KeStackAttachProcess(process, &apc);
    status = MmCopyMemory((PVOID)(result + 1), source, size, MM_COPY_MEMORY_VIRTUAL, &copied);
    KeUnstackDetachProcess(&apc);

    result->status = status;
    result->transferred = (ULONG)copied;
    irp->IoStatus.Information = sizeof(WH_RESULT) + copied;
    return STATUS_SUCCESS;
}
static NTSTATUS WhBase(PIRP irp, PIO_STACK_LOCATION stack)
{
    PEPROCESS process;
    PVOID base;

    if (stack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(PVOID) ||
        !irp->AssociatedIrp.SystemBuffer) return STATUS_BUFFER_TOO_SMALL;
    process = WhGetProcess(stack->FileObject);
    if (!process) return STATUS_INVALID_DEVICE_STATE;
    base = PsGetProcessSectionBaseAddress(process);
    if (!base) return STATUS_NOT_FOUND;
    *(PVOID*)irp->AssociatedIrp.SystemBuffer = base;
    irp->IoStatus.Information = sizeof(PVOID);
    return STATUS_SUCCESS;
}

static NTSTATUS WhDetach(PIO_STACK_LOCATION stack)
{
    WhPutProcess(stack->FileObject);
    return STATUS_SUCCESS;
}

static NTSTATUS WhDispatch(PDEVICE_OBJECT device, PIRP irp)
{
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(irp);
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR information = 0;

    UNREFERENCED_PARAMETER(device);

    switch (stack->MajorFunction) {
    case IRP_MJ_CREATE:
        stack->FileObject->FsContext = NULL;
        status = STATUS_SUCCESS;
        break;
    case IRP_MJ_CLEANUP:
    case IRP_MJ_CLOSE:
        WhPutProcess(stack->FileObject);
        status = STATUS_SUCCESS;
        break;
    case IRP_MJ_DEVICE_CONTROL:
        if (irp->RequestorMode != UserMode) { status = STATUS_INVALID_DEVICE_STATE; break; }
        switch (stack->Parameters.DeviceIoControl.IoControlCode) {
        case WH_IOCTL_ATTACH: status = WhAttach(irp, stack); break;
        case WH_IOCTL_READ:
            status = WhRead(irp, stack);
            information = irp->IoStatus.Information;
            break;
        case WH_IOCTL_DETACH: status = WhDetach(stack); break;
        case WH_IOCTL_BASE:
            status = WhBase(irp, stack);
            information = irp->IoStatus.Information;
            break;
        default: status = STATUS_INVALID_DEVICE_REQUEST; break;
        }
        break;
    default:
        break;
    }

    irp->IoStatus.Status = status;
    irp->IoStatus.Information = information;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return status;
}

static VOID WhUnload(PDRIVER_OBJECT driver)
{
    UNICODE_STRING link = RTL_CONSTANT_STRING(L"\\DosDevices\\WhReader4");
    IoDeleteSymbolicLink(&link);
    if (driver->DeviceObject) IoDeleteDevice(driver->DeviceObject);
}

static NTSTATUS WhInitialize(PDRIVER_OBJECT driver, PUNICODE_STRING registry)
{
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"\\Device\\WhReader4");
    UNICODE_STRING link = RTL_CONSTANT_STRING(L"\\DosDevices\\WhReader4");
    UNICODE_STRING acl = RTL_CONSTANT_STRING(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    PDEVICE_OBJECT device = NULL;
    NTSTATUS status;
    ULONG i;

    UNREFERENCED_PARAMETER(registry);
    for (i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; ++i) driver->MajorFunction[i] = WhDispatch;
    driver->DriverUnload = WhUnload;

    status = IoCreateDeviceSecure(driver, 0, &name, FILE_DEVICE_UNKNOWN,
        FILE_DEVICE_SECURE_OPEN, FALSE, &acl, &WhClass, &device);
    if (!NT_SUCCESS(status)) return status;

    status = IoCreateSymbolicLink(&link, &name);
    if (!NT_SUCCESS(status)) { IoDeleteDevice(device); return status; }

    device->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}

NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registry)
{
    UNREFERENCED_PARAMETER(driver);
    UNREFERENCED_PARAMETER(registry);
    return IoCreateDriver(NULL, WhInitialize);
}


