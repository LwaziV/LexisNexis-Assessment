import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

type OrderStatus = 'Pending' | 'Confirmed' | 'Fulfilled' | 'Cancelled';

interface OrderLine {
  sku: string;
  name: string;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
}

interface Order {
  id: string;
  externalReference: string;
  customerName: string;
  customerEmail: string;
  currency: string;
  notes: string | null;
  items: OrderLine[];
  subtotal: number;
  total: number;
  status: OrderStatus;
  createdAt: string;
  updatedAt: string;
}

interface PagedOrders {
  items: Order[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

@Component({
  selector: 'app-root',
  imports: [DatePipe, ReactiveFormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {
  readonly maxItems = 50;
  private readonly http = inject(HttpClient);
  private readonly fb = inject(FormBuilder);

  readonly orders = signal<Order[]>([]);
  readonly page = signal(1);
  readonly pageSize = signal(10);
  readonly totalCount = signal(0);
  readonly totalPages = signal(1);
  readonly pageSizes = [10, 25, 50];
  readonly pageStart = computed(() => this.totalCount() === 0 ? 0 : (this.page() - 1) * this.pageSize() + 1);
  readonly pageEnd = computed(() => Math.min(this.page() * this.pageSize(), this.totalCount()));
  readonly selectedId = signal<string | null>(null);
  readonly showOrderModal = signal(false);
  readonly selectedOrder = computed(() =>
    this.orders().find(order => order.id === this.selectedId()) ?? null
  );
  readonly loading = signal(true);
  readonly submitting = signal(false);
  readonly notice = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly form = this.fb.group({
    externalReference: ['', [Validators.required, Validators.maxLength(80)]],
    customerName: ['', [Validators.required, Validators.maxLength(160)]],
    customerEmail: ['', [Validators.required, Validators.email]],
    currency: ['ZAR', [Validators.required, Validators.pattern(/^[a-zA-Z]{3}$/)]],
    notes: ['', Validators.maxLength(2000)],
    items: this.fb.array([this.createItem()])
  });

  constructor() {
    this.loadOrders();
  }

  get items(): FormArray {
    return this.form.controls.items;
  }

  addItem(): void {
    if (this.items.length < this.maxItems) this.items.push(this.createItem());
  }

  removeItem(index: number): void {
    if (this.items.length > 1) this.items.removeAt(index);
  }

  submitOrder(): void {
    this.notice.set(null);
    this.error.set(null);
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) return;

    this.submitting.set(true);
    this.http.post<Order>('/api/orders', this.form.getRawValue(), { observe: 'response' }).subscribe({
      next: response => {
        const order = response.body!;
        this.notice.set(response.status === 201
          ? `Order ${order.externalReference} submitted.`
          : `Reference ${order.externalReference} is already on file. Showing the original order.`);
        this.form.reset({
          externalReference: '', customerName: '', customerEmail: '', currency: 'ZAR', notes: ''
        });
        while (this.items.length > 1) this.items.removeAt(1);
        this.items.at(0).reset({ sku: '', name: '', quantity: 1, unitPrice: 0 });
        this.page.set(1);
        this.loadOrders(order.id);
        this.selectedId.set(order.id);
        this.showOrderModal.set(true);
        this.submitting.set(false);
      },
      error: failure => {
        this.error.set(this.messageFrom(failure, 'The order could not be submitted.'));
        this.submitting.set(false);
      }
    });
  }

  openOrder(order: Order): void {
    this.selectedId.set(order.id);
    this.showOrderModal.set(true);
  }

  closeOrderModal(): void {
    this.showOrderModal.set(false);
  }

  changeStatus(order: Order, status: OrderStatus): void {
    this.error.set(null);
    this.http.patch<Order>(`/api/orders/${order.id}/status`, { status }).subscribe({
      next: updated => {
        this.orders.update(current =>
          current.map(item => item.id === updated.id ? updated : item));
        if (this.selectedId() === updated.id) {
          this.selectedId.set(updated.id);
        }
      },
      error: failure => this.error.set(this.messageFrom(failure, 'The status could not be changed.'))
    });
  }

  nextStatuses(status: OrderStatus): OrderStatus[] {
    if (status === 'Pending') return ['Confirmed', 'Cancelled'];
    if (status === 'Confirmed') return ['Fulfilled', 'Cancelled'];
    return [];
  }

  formatMoney(amount: number, currency: string): string {
    try {
      return new Intl.NumberFormat('en-ZA', { style: 'currency', currency }).format(amount);
    } catch {
      return `${currency} ${amount.toFixed(2)}`;
    }
  }

  loadOrders(selectId?: string): void {
    this.loading.set(true);
    const params = { page: this.page(), pageSize: this.pageSize() };
    this.http.get<PagedOrders>('/api/orders', { params }).subscribe({
      next: result => {
        this.orders.set(result.items);
        this.page.set(result.page);
        this.pageSize.set(result.pageSize);
        this.totalCount.set(result.totalCount);
        this.totalPages.set(result.totalPages);
        const selected = selectId ?? this.selectedId();
        this.selectedId.set(result.items.some(order => order.id === selected)
          ? selected
          : result.items[0]?.id ?? null);
        this.loading.set(false);
      },
      error: failure => {
        this.error.set(this.messageFrom(failure, 'Unable to load orders.'));
        this.loading.set(false);
      }
    });
  }

  goToPage(page: number): void {
    if (page < 1 || page > this.totalPages() || page === this.page()) return;
    this.page.set(page);
    this.loadOrders();
  }

  setPageSize(pageSize: number): void {
    this.pageSize.set(pageSize);
    this.page.set(1);
    this.loadOrders();
  }

  private createItem() {
    return this.fb.group({
      sku: ['', [Validators.required, Validators.maxLength(64)]],
      name: ['', [Validators.required, Validators.maxLength(160)]],
      quantity: [1, [Validators.required, Validators.min(1), Validators.pattern(/^\d+$/)]],
      unitPrice: [0, [Validators.required, Validators.min(0)]]
    });
  }

  private messageFrom(failure: HttpErrorResponse, fallback: string): string {
    return failure.error?.detail ?? failure.error?.title ?? fallback;
  }
}
